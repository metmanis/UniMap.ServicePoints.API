using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Isochrones;
using UniMap.ServicePoints.API.ServicePoints;

namespace UniMap.ServicePoints.API.Gaps;

public class GapService(
    NpgsqlDataSource dataSource,
    IIsochroneService isochrones,
    GapCache cache,
    AreaLimit areaLimit,
    IOptions<ServicePointLimits> limits,
    IOptions<GapOptions> options,
    ILogger<GapService> logger) : IGapService
{
    private const string InsufficientDataMessage = "Not enough spatial data";

    /// <summary>Rounds of fetch and calculate before giving up when points keep changing during the calculation.</summary>
    public const int MaxCalculationAttempts = 3;

    private const int Srid = 4326;
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), Srid);

    // The geography cast of a stored point raises an error for out-of-range coordinates, so it is guarded by the valid
    // location test itself and never depends on the planner running the WHERE filter first.
    // Points linked to the vehicle. Every use adds ServicePointQuery.Visible: active with a valid geometry.
    private const string EligiblePoints = """
        "Services"."ServicePoints" sp
        JOIN "Services"."ServicePointVehicleTypes" vt ON vt."ServicePointId" = sp."Id" AND vt."VehicleType" = @vehicle_link
        """;

    // Candidates are chosen per range by straight-line radius, and their stored rows are joined from the cache
    // table. A candidate with no stored row has a NULL computed_at. The radius only chooses whose rows to read.
    // The area's edges are straight in longitude and latitude, while a geography edge is a great circle that bows
    // away from them (17 km on a 1,040 km edge). Vertices every 0.1 degrees keep the two within about a metre.
    private const string CandidatesAndStoredRows = $"""
        WITH ranges AS (
            SELECT * FROM unnest(@range_types::text[], @range_values::int[], @radii::float8[]) AS r(range_type, range_value, radius_m)
        ),
        candidates AS (
            SELECT sp."Id" AS id, sp."Geometry" AS location, r.range_type, r.range_value
            FROM {EligiblePoints}
            JOIN ranges r ON ST_DWithin(CASE WHEN {ServicePointQuery.ValidLocation} THEN sp."Geometry"::geography END, ST_Segmentize(@area, 0.1)::geography, r.radius_m)
            WHERE {ServicePointQuery.Visible}
        ),
        stored AS (
            SELECT c.id, c.range_type, c.range_value, i."ComputedAt" AS computed_at, i."Geometry" AS geometry
            FROM candidates c
            LEFT JOIN "Services"."ServicePointIsochrones" i
                ON i."ServicePointId" = c.id AND i."VehicleType" = @vehicle_row
               AND i."RangeType" = c.range_type AND i."RangeValue" = c.range_value
        )
        """;

    private const string Fingerprint = """
        SELECT encode(sha256(convert_to(COALESCE(string_agg(
                   id::text || ':' || range_type || ':' || range_value::text || ':' || COALESCE(extract(epoch FROM computed_at)::text, 'missing'),
                   ',' ORDER BY id, range_type), ''), 'UTF8')), 'hex')
        FROM stored
        """;

    public async Task<GapResult> CalculateAsync(GapRequest request, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0) throw new GapValidationException(errors);

        var area = await RepairAreaAsync(request.Area, ct);
        if (area.IsEmpty || area.Area == 0)
            throw new GapValidationException(["The area has no surface: it must enclose a real region."]);

        // Before any data work or HERE call: a drawn area bigger than the largest city is refused outright.
        if (await areaLimit.GetMaxAreaKm2Async(ct) is { } maxKm2)
        {
            var drawnKm2 = await MeasureKm2Async(area, ct);
            if (drawnKm2 > maxKm2)
                throw new GapValidationException([TooLargeMessage(drawnKm2, maxKm2)]);
        }

        if (!await HasSufficientDataAsync(request.Vehicle, ct))
            return new GapResult(false, InsufficientDataMessage, null);

        // Read on every request, because these points are nationwide and are not part of the cached gap's fingerprint.
        var excluded = await CountExcludedPointsAsync(request.Vehicle, ct);

        var query = new GapQuery(area, request.Vehicle, Ranges(request));
        var fingerprint = await ReadFingerprintAsync(query, ct);

        var areaHash = Convert.ToHexString(SHA256.HashData(request.Area.AsBinary()));
        GapCacheKey Key(string fp) => new(areaHash, request.Vehicle, query.Seconds, query.Metres, fp);

        if (cache.TryGet(Key(fingerprint), out var cached))
            return new GapResult(true, null, cached, excluded);

        for (var attempt = 1; attempt <= MaxCalculationAttempts; attempt++)
        {
            await FetchIsochronesAsync(query, ct);

            var result = await ComputeGapAsync(query, ct);
            if (result.NoAreaRowPoints.Length > 0)
            {
                // Such a row would read as reaching nothing and report a false gap. It stays until it is removed.
                logger.LogWarning("Gap calculation stopped: stored isochrones with no area for service points {ServicePointIds}",
                    string.Join(", ", result.NoAreaRowPoints));
                throw new NoReachableAreaException(result.NoAreaRowPoints[0]);
            }

            if (result.MissingRows == 0)
            {
                // Under the fingerprint of the rows this result used, which is what the next identical request will read.
                cache.Set(Key(result.Fingerprint), result.Gaps);
                return new GapResult(true, null, result.Gaps, excluded);
            }

            // A point was added or moved after the fetch. Calculating without its reach would report a false gap.
            logger.LogWarning("Gap calculation attempt {Attempt}: {MissingRows} isochrone rows went missing during the calculation",
                attempt, result.MissingRows);
        }

        throw new GapCalculationUnstableException();
    }

    private List<string> Validate(GapRequest request)
    {
        var errors = new List<string>(GapRequestValidator.Validate(request, limits.Value));

        if (request.Area.IsEmpty)
            errors.Add("The area must not be empty.");
        else if (request.Area.Coordinates.Any(c => !(c.X is >= -180 and <= 180) || !(c.Y is >= -90 and <= 90)))
            errors.Add("The area coordinates must be longitude between -180 and 180 and latitude between -90 and 90.");

        return errors;
    }

    private async Task<Geometry> RepairAreaAsync(Polygon polygon, CancellationToken ct)
    {
        var area = polygon.Copy();
        area.SRID = Srid;

        // Repair can return a MultiPolygon or a GeometryCollection, and a polygon that collapses comes back empty.
        await using var cmd = dataSource.CreateCommand("SELECT ST_CollectionExtract(ST_MakeValid(@area), 3)");
        AddGeometry(cmd, "area", area);
        return (Geometry)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static string TooLargeMessage(double drawnKm2, double maxKm2) => string.Create(CultureInfo.InvariantCulture,
        $"The drawn area is {drawnKm2:N1} km², which is larger than the largest city ({maxKm2:N1} km²). Draw a smaller area.");

    private async Task<double> MeasureKm2Async(Geometry area, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {GapMeasure.SquareKmSql("@area")}");
        AddGeometry(cmd, "area", area);
        return (double)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> HasSufficientDataAsync(VehicleType vehicle, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT EXISTS (SELECT 1 FROM {EligiblePoints} WHERE {ServicePointQuery.Visible})");
        cmd.Parameters.AddWithValue("vehicle_link", vehicle.ToString());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<int> CountExcludedPointsAsync(VehicleType vehicle, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT count(*) FROM {EligiblePoints} WHERE {ServicePointQuery.Active} AND ({ServicePointQuery.ValidLocation}) IS NOT TRUE");
        cmd.Parameters.AddWithValue("vehicle_link", vehicle.ToString());
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private List<GapRange> Ranges(GapRequest request)
    {
        var ranges = new List<GapRange>(2);
        if (request.DriveTimeMinutes is { } minutes)
        {
            var seconds = minutes * 60;
            ranges.Add(new GapRange(RangeType.Time, seconds, seconds * options.Value.MaxSpeedKmh / 3.6));
        }
        if (request.DistanceKm is { } km)
        {
            var metres = km * 1000;
            ranges.Add(new GapRange(RangeType.Distance, metres, metres));
        }
        return ranges;
    }

    private async Task<string> ReadFingerprintAsync(GapQuery query, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            {CandidatesAndStoredRows}
            SELECT ({Fingerprint}),
                   (SELECT count(*) FROM candidates WHERE range_type = @time_text),
                   (SELECT count(*) FROM candidates WHERE range_type = @distance_text)
            """);
        query.AddParameters(cmd);
        cmd.Parameters.AddWithValue("time_text", IsochroneService.ToText(RangeType.Time));
        cmd.Parameters.AddWithValue("distance_text", IsochroneService.ToText(RangeType.Distance));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        foreach (var range in query.Ranges)
        {
            logger.LogInformation("Gap calculation for {Vehicle}: {CandidateCount} candidates for range type {RangeType}, value {RangeValue}",
                query.Vehicle, reader.GetInt64(range.Type == RangeType.Time ? 1 : 2), range.Type, range.Value);
        }
        return reader.GetString(0);
    }

    private async Task FetchIsochronesAsync(GapQuery query, CancellationToken ct)
    {
        foreach (var range in query.Ranges)
        {
            await using var cmd = dataSource.CreateCommand($"""
                {CandidatesAndStoredRows}
                SELECT id, ST_X(location), ST_Y(location) FROM candidates WHERE range_type = @range_type ORDER BY id
                """);
            query.AddParameters(cmd);
            cmd.Parameters.AddWithValue("range_type", IsochroneService.ToText(range.Type));

            var targets = new List<ServicePointCoverageTarget>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    targets.Add(new ServicePointCoverageTarget(reader.GetInt32(0), new Coordinate(reader.GetDouble(1), reader.GetDouble(2))));
            }

            await isochrones.GetOrComputeAsync(targets, query.Vehicle, range.Type, range.Value, ct);
        }
    }

    /// <summary>
    /// One statement, so the gap, the fingerprint and the row checks all describe the same snapshot.
    /// A piece is measured along its drawn straight edges: a long thin piece has edges a few metres apart, so
    /// its vertices are every 0.01 degrees, where the bow is about a centimetre. An empty geometry has area 0.
    /// </summary>
    private async Task<(MultiPolygon Gaps, string Fingerprint, long MissingRows, int[] NoAreaRowPoints)> ComputeGapAsync(GapQuery query, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            {CandidatesAndStoredRows},
            repaired AS (
                SELECT id, ST_CollectionExtract(ST_MakeValid(geometry), 3) AS geometry
                FROM stored
                WHERE geometry IS NOT NULL
            ),
            reach AS (
                SELECT ST_Union(geometry) AS geometry
                FROM repaired
                WHERE ST_Intersects(geometry, @area)
            ),
            pieces AS (
                SELECT (ST_Dump(COALESCE(ST_Difference(@area, reach.geometry), @area))).geom AS piece
                FROM reach
            )
            SELECT ({Fingerprint}),
                   (SELECT count(*) FROM stored WHERE computed_at IS NULL),
                   (SELECT ST_Multi(ST_Collect(piece)) FROM pieces
                    WHERE {GapMeasure.SquareKmSql("piece")} >= @min_square_km),
                   ARRAY(SELECT DISTINCT id FROM repaired WHERE ST_Area(geometry) = 0 ORDER BY id)
            """);
        query.AddParameters(cmd);
        cmd.Parameters.AddWithValue("min_square_km", ServicePointLimits.MinGapSquareKm);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var gaps = await reader.IsDBNullAsync(2, ct) ? Factory.CreateMultiPolygon() : reader.GetFieldValue<MultiPolygon>(2);
        return (gaps, reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<int[]>(3));
    }

    private static void AddGeometry(NpgsqlCommand cmd, string name, Geometry geometry) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Geometry) { Value = geometry });

    private sealed record GapRange(RangeType Type, int Value, double RadiusMetres);

    private sealed record GapQuery(Geometry Area, VehicleType Vehicle, List<GapRange> Ranges)
    {
        public int? Seconds => Ranges.FirstOrDefault(r => r.Type == RangeType.Time)?.Value;
        public int? Metres => Ranges.FirstOrDefault(r => r.Type == RangeType.Distance)?.Value;

        public void AddParameters(NpgsqlCommand cmd)
        {
            AddGeometry(cmd, "area", Area);
            cmd.Parameters.AddWithValue("vehicle_link", Vehicle.ToString());
            cmd.Parameters.AddWithValue("vehicle_row", IsochroneService.ToText(Vehicle));
            cmd.Parameters.AddWithValue("range_types", Ranges.Select(r => IsochroneService.ToText(r.Type)).ToArray());
            cmd.Parameters.AddWithValue("range_values", Ranges.Select(r => r.Value).ToArray());
            cmd.Parameters.AddWithValue("radii", Ranges.Select(r => r.RadiusMetres).ToArray());
        }
    }
}
