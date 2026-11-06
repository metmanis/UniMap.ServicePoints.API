using NetTopologySuite.Geometries;
using Npgsql;

namespace UniMap.ServicePoints.API.ServicePoints;

/// <summary>Reads where a service point is, for the services that ask HERE about it.</summary>
public static class ServicePointLocationReader
{
    /// <returns>The location, or null when the point is unknown, inactive or has no valid location.</returns>
    public static async Task<Coordinate?> ReadAsync(NpgsqlDataSource dataSource, int id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT ST_X(sp."Geometry"), ST_Y(sp."Geometry")
            FROM "Services"."ServicePoints" sp
            WHERE sp."Id" = @id AND {ServicePointQuery.Visible}
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Coordinate(reader.GetDouble(0), reader.GetDouble(1)) : null;
    }
}
