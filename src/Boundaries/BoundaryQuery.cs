using Microsoft.Extensions.Options;
using Npgsql;

namespace UniMap.ServicePoints.API.Boundaries;

public enum BoundaryKind { City, District }

public class BoundaryQuery(NpgsqlDataSource dataSource, IOptions<BoundaryOptions> options)
{
    // Table names are fixed constants chosen by the enum, never built from request text. Stored SRID is 4326, so no transform.
    private static string Table(BoundaryKind kind) => kind switch
    {
        BoundaryKind.City => "\"Administrative\".\"Cities\"",
        BoundaryKind.District => "\"Administrative\".\"Districts\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A GeoJSON Feature (id, name, simplified polygon) or null when the id is unknown or has no geometry.</summary>
    public async Task<string?> GetBoundaryGeoJsonAsync(BoundaryKind kind, int id, CancellationToken ct)
    {
        var tolerance = options.Value.SimplifyToleranceDegrees;

        await using var cmd = dataSource.CreateCommand($"""
            SELECT jsonb_build_object(
                'type', 'Feature',
                'properties', jsonb_build_object('id', t."Id", 'name', t."Name"),
                'geometry', ST_AsGeoJSON(ST_SimplifyPreserveTopology(t."Geometry", @tolerance))::jsonb
            )::text
            FROM {Table(kind)} t
            WHERE t."Id" = @id AND t."Geometry" IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tolerance", tolerance);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
