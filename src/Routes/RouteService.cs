using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.ServicePoints;

namespace UniMap.ServicePoints.API.Routes;

public record RouteResult(int From, int To, double DistanceMeters, int DurationSeconds,
    LineString Geometry, DateTimeOffset CalculatedAt, bool FromCache);

/// <summary>
/// Driving route from one service point to another (one row per direction). A saved route is reused while it is
/// at most <see cref="ValidMonths"/> months old and neither end has moved; otherwise HERE is asked and the row replaced.
/// </summary>
public class RouteService(NpgsqlDataSource dataSource, IHereRoutingClient here, TimeProvider time)
{
    public const int ValidMonths = 6;

    /// <returns>Null when either point is unknown, inactive or has no valid location.</returns>
    /// <exception cref="HereNoRouteException">HERE found no route. Nothing is saved.</exception>
    /// <exception cref="HereUnavailableException">HERE failed. Nothing is saved and an expired route is not returned.</exception>
    public async Task<RouteResult?> GetRouteAsync(int from, int to, CancellationToken ct)
    {
        var start = await ServicePointLocationReader.ReadAsync(dataSource, from, ct);
        if (start is null) return null;
        var end = await ServicePointLocationReader.ReadAsync(dataSource, to, ct);
        if (end is null) return null;

        var saved = await ReadSavedAsync(from, to, start, end, ct);
        if (saved is not null && time.GetUtcNow().UtcDateTime <= saved.CalculatedAt.UtcDateTime.AddMonths(ValidMonths))
            return saved;

        var route = await here.GetDrivingRouteAsync(start, end, ct);
        var calculatedAt = time.GetUtcNow();
        await SaveAsync(from, to, start, end, route, calculatedAt, ct);
        return new RouteResult(from, to, route.DistanceMeters, route.DurationSeconds, route.Geometry, calculatedAt, false);
    }

    /// <summary>The saved route, or null when there is none or either end has moved since it was saved.</summary>
    private async Task<RouteResult?> ReadSavedAsync(int from, int to, Coordinate start, Coordinate end, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT "FromX", "FromY", "ToX", "ToY", "Geometry", "DistanceMeters", "DurationSeconds", "CalculatedAt"
            FROM "Services"."ServicePointRoutes"
            WHERE "FromServicePointId" = @from AND "ToServicePointId" = @to
            """);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        if (reader.GetDouble(0) != start.X || reader.GetDouble(1) != start.Y ||
            reader.GetDouble(2) != end.X || reader.GetDouble(3) != end.Y)
            return null;

        return new RouteResult(from, to, reader.GetDouble(5), reader.GetInt32(6),
            reader.GetFieldValue<LineString>(4), reader.GetFieldValue<DateTimeOffset>(7), true);
    }

    private async Task SaveAsync(int from, int to, Coordinate start, Coordinate end, DrivingRoute route,
        DateTimeOffset calculatedAt, CancellationToken ct)
    {
        // Two requests can finish together; the last writer wins and both succeed.
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO "Services"."ServicePointRoutes"
                ("FromServicePointId", "ToServicePointId", "FromX", "FromY", "ToX", "ToY",
                 "Geometry", "DistanceMeters", "DurationSeconds", "CalculatedAt")
            VALUES (@from, @to, @fx, @fy, @tx, @ty, @geometry, @distance, @duration, @at)
            ON CONFLICT ("FromServicePointId", "ToServicePointId") DO UPDATE SET
                "FromX" = EXCLUDED."FromX", "FromY" = EXCLUDED."FromY", "ToX" = EXCLUDED."ToX", "ToY" = EXCLUDED."ToY",
                "Geometry" = EXCLUDED."Geometry", "DistanceMeters" = EXCLUDED."DistanceMeters",
                "DurationSeconds" = EXCLUDED."DurationSeconds", "CalculatedAt" = EXCLUDED."CalculatedAt"
            """);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("fx", start.X);
        cmd.Parameters.AddWithValue("fy", start.Y);
        cmd.Parameters.AddWithValue("tx", end.X);
        cmd.Parameters.AddWithValue("ty", end.Y);
        cmd.Parameters.Add(new NpgsqlParameter("geometry", NpgsqlDbType.Geometry) { Value = route.Geometry });
        cmd.Parameters.AddWithValue("distance", route.DistanceMeters);
        cmd.Parameters.AddWithValue("duration", route.DurationSeconds);
        cmd.Parameters.AddWithValue("at", calculatedAt.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
