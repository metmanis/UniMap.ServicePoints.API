using System.Runtime.ExceptionServices;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.API.Isochrones;

public class IsochroneService(NpgsqlDataSource dataSource, IHereRoutingClient here, ILogger<IsochroneService> logger)
    : IIsochroneService
{
    public const int MaxConcurrentHereCalls = 8;

    private const int Srid = 4326;

    public async Task<IReadOnlyList<ServicePointIsochrone>> GetOrComputeAsync(
        IReadOnlyList<ServicePointCoverageTarget> points, VehicleType vehicle, RangeType rangeType, int rangeValue,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return [];

        var targets = points.DistinctBy(p => p.ServicePointId).ToList();
        var vehicleText = ToText(vehicle);
        var rangeText = ToText(rangeType);

        var geometries = await ReadCachedAsync(targets, vehicleText, rangeText, rangeValue, ct);
        var cachedIds = geometries.Keys.ToList();
        var missing = targets.Where(t => !geometries.ContainsKey(t.ServicePointId)).ToList();

        if (missing.Count > 0)
        {
            var computed = await ComputeMissingAsync(missing, vehicle, rangeType, vehicleText, rangeText, rangeValue, ct);
            foreach (var (id, geometry) in computed) geometries[id] = geometry;
        }

        // Only after every point succeeded: a failed call leaves cache-hit points flagged for the next attempt.
        if (cachedIds.Count > 0) await ClearStaleAsync(cachedIds, ct);

        return targets
            .Where(t => geometries.ContainsKey(t.ServicePointId))
            .Select(t => new ServicePointIsochrone(t.ServicePointId, rangeType, rangeValue, geometries[t.ServicePointId]))
            .ToList();
    }

    private async Task<Dictionary<int, MultiPolygon>> ReadCachedAsync(
        List<ServicePointCoverageTarget> targets, string vehicleText, string rangeText, int rangeValue, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT "ServicePointId", "Geometry" FROM "Services"."ServicePointIsochrones"
            WHERE "ServicePointId" = ANY(@ids) AND "VehicleType" = @vehicle AND "RangeType" = @rangeType AND "RangeValue" = @value
            """);
        cmd.Parameters.AddWithValue("ids", targets.Select(t => t.ServicePointId).ToArray());
        cmd.Parameters.AddWithValue("vehicle", vehicleText);
        cmd.Parameters.AddWithValue("rangeType", rangeText);
        cmd.Parameters.AddWithValue("value", rangeValue);

        var found = new Dictionary<int, MultiPolygon>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found[reader.GetInt32(0)] = reader.GetFieldValue<MultiPolygon>(1);
        return found;
    }

    private async Task<Dictionary<int, MultiPolygon>> ComputeMissingAsync(
        List<ServicePointCoverageTarget> missing, VehicleType vehicle, RangeType rangeType, string vehicleText,
        string rangeText, int rangeValue, CancellationToken ct)
    {
        var results = new Dictionary<int, MultiPolygon>();
        using var gate = new SemaphoreSlim(MaxConcurrentHereCalls);
        ExceptionDispatchInfo? failure = null;
        var stop = false;

        void Fail(Exception ex)
        {
            Volatile.Write(ref stop, true);
            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
        }

        async Task RunAsync(ServicePointCoverageTarget target)
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }

            try
            {
                if (Volatile.Read(ref stop)) return;
                var stored = await ComputeOneAsync(target, vehicle, rangeType, vehicleText, rangeText, rangeValue, ct);
                if (stored is not null) lock (results) results[target.ServicePointId] = stored;
            }
            catch (Exception ex)
            {
                Fail(ex); // before the release below, so a queued waiter always sees the stop flag
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(missing.Select(RunAsync));
        failure?.Throw();
        return results;
    }

    /// <summary>The stored reach, or null when the point moved or went away during the HERE call, so nothing was stored.</summary>
    private async Task<MultiPolygon?> ComputeOneAsync(
        ServicePointCoverageTarget target, VehicleType vehicle, RangeType rangeType, string vehicleText,
        string rangeText, int rangeValue, CancellationToken ct)
    {
        Isoline isoline;
        try
        {
            var lines = await here.GetIsolinesAsync(target.Location, vehicle, rangeType, [rangeValue], ct);
            isoline = lines.FirstOrDefault(l => l.RangeValue == rangeValue)
                ?? throw new HereEmptyIsolineException("HERE returned no polygon for the requested range value.");
        }
        catch (HereEmptyIsolineException ex)
        {
            logger.LogWarning(
                "HERE found no reach for service point {ServicePointId}, vehicle {Vehicle}, range type {RangeType}, value {RangeValue}: {Reason}",
                target.ServicePointId, vehicle, rangeType, rangeValue, ex.Message);
            throw new NoReachableAreaException(target.ServicePointId);
        }
        catch (HereUnavailableException ex)
        {
            logger.LogWarning(
                "HERE isoline failed for service point {ServicePointId}, vehicle {Vehicle}, range type {RangeType}, value {RangeValue}: {Reason}",
                target.ServicePointId, vehicle, rangeType, rangeValue, ex.Message);
            throw;
        }

        var geometry = isoline.Geometry.Copy();
        geometry.SRID = Srid;

        if (!await HasAreaAsync(geometry, ct))
        {
            logger.LogWarning(
                "HERE isoline has no area for service point {ServicePointId}, vehicle {Vehicle}, range type {RangeType}, value {RangeValue}",
                target.ServicePointId, vehicle, rangeType, rangeValue);
            throw new NoReachableAreaException(target.ServicePointId);
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The reach is stored only if the point is still where HERE was asked about. X and Y are the exact values the
        // target was read as. The row lock closes the race with a move that has not committed: the insert waits and
        // then re-checks the location, and a later move waits for this commit, so its trigger sees and deletes this row.
        // NO KEY UPDATE is the level the stale flag update below needs anyway, so two calculations cannot deadlock on an upgrade.
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "Services"."ServicePointIsochrones"
                ("ServicePointId", "VehicleType", "RangeType", "RangeValue", "Geometry", "ComputedAt")
            SELECT sp."Id", @vehicle, @rangeType, @value, @geometry, now()
            FROM "Services"."ServicePoints" sp
            WHERE sp."Id" = @id AND ST_X(sp."Geometry") = @x AND ST_Y(sp."Geometry") = @y
            FOR NO KEY UPDATE
            ON CONFLICT ("ServicePointId", "VehicleType", "RangeType", "RangeValue") DO NOTHING
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("x", target.Location.X);
            insert.Parameters.AddWithValue("y", target.Location.Y);
            insert.Parameters.AddWithValue("id", target.ServicePointId);
            insert.Parameters.AddWithValue("vehicle", vehicleText);
            insert.Parameters.AddWithValue("rangeType", rangeText);
            insert.Parameters.AddWithValue("value", rangeValue);
            insert.Parameters.Add(new NpgsqlParameter("geometry", NpgsqlDbType.Geometry) { Value = geometry });
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var clear = new NpgsqlCommand(
            """UPDATE "Services"."ServicePoints" SET "IsStale" = false WHERE "Id" = @id AND ST_X("Geometry") = @x AND ST_Y("Geometry") = @y""", conn, tx))
        {
            clear.Parameters.AddWithValue("id", target.ServicePointId);
            clear.Parameters.AddWithValue("x", target.Location.X);
            clear.Parameters.AddWithValue("y", target.Location.Y);
            await clear.ExecuteNonQueryAsync(ct);
        }

        // Return what is stored, so a concurrent calculation that won the insert gives both callers the same polygon.
        MultiPolygon? stored;
        await using (var select = new NpgsqlCommand("""
            SELECT "Geometry" FROM "Services"."ServicePointIsochrones"
            WHERE "ServicePointId" = @id AND "VehicleType" = @vehicle AND "RangeType" = @rangeType AND "RangeValue" = @value
            """, conn, tx))
        {
            select.Parameters.AddWithValue("id", target.ServicePointId);
            select.Parameters.AddWithValue("vehicle", vehicleText);
            select.Parameters.AddWithValue("rangeType", rangeText);
            select.Parameters.AddWithValue("value", rangeValue);
            stored = (MultiPolygon?)await select.ExecuteScalarAsync(ct);
        }

        await tx.CommitAsync(ct);

        if (stored is null)
        {
            // Left out of the result. The gap calculation sees the missing row and fetches again from the new location.
            logger.LogInformation(
                "HERE isoline not stored for service point {ServicePointId}: the point moved during the call, vehicle {Vehicle}, range type {RangeType}, value {RangeValue}",
                target.ServicePointId, vehicle, rangeType, rangeValue);
            return null;
        }

        logger.LogInformation(
            "HERE isoline stored for service point {ServicePointId}, vehicle {Vehicle}, range type {RangeType}, value {RangeValue}",
            target.ServicePointId, vehicle, rangeType, rangeValue);
        return stored;
    }

    // Measured after repair, as the gap calculation reads it. The two lobes of a bow-tie can cancel out in the raw ring.
    private async Task<bool> HasAreaAsync(Geometry geometry, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT ST_Area(ST_CollectionExtract(ST_MakeValid(@geometry), 3)) > 0");
        cmd.Parameters.Add(new NpgsqlParameter("geometry", NpgsqlDbType.Geometry) { Value = geometry });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task ClearStaleAsync(List<int> ids, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""UPDATE "Services"."ServicePoints" SET "IsStale" = false WHERE "Id" = ANY(@ids)""");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The one mapping from enums to database and HERE text.
    public static string ToText(VehicleType vehicle) => vehicle switch
    {
        VehicleType.PassengerCar => "car",
        VehicleType.HeavyDuty => "truck",
        _ => throw new ArgumentOutOfRangeException(nameof(vehicle), vehicle, null),
    };

    public static string ToText(RangeType rangeType) => rangeType switch
    {
        RangeType.Time => "time",
        RangeType.Distance => "distance",
        _ => throw new ArgumentOutOfRangeException(nameof(rangeType), rangeType, null),
    };
}
