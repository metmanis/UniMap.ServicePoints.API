using Npgsql;

namespace UniMap.ServicePoints.API.Gaps;

/// <summary>
/// The largest area a user may draw: the area of the largest city in Administrative."Cities". Computing it measures
/// every city boundary, which is expensive, and boundaries rarely change, so the result is kept for one hour.
/// </summary>
public class AreaLimit(NpgsqlDataSource dataSource, TimeProvider time, ILogger<AreaLimit> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (double? Km2, DateTimeOffset Expires)? _cached;

    /// <summary>The largest city's area in km2, or null when no city can be measured (the limit is then not enforced).</summary>
    public async Task<double?> GetMaxAreaKm2Async(CancellationToken ct)
    {
        if (TryGetCached(out var cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (TryGetCached(out cached)) return cached;

            var km2 = await MeasureLargestCityAsync(ct);
            if (km2 is null)
                logger.LogWarning("Administrative.Cities has no usable boundary, so the largest drawn area is not limited");
            _cached = (km2, time.GetUtcNow() + CacheDuration);
            return km2;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetCached(out double? km2)
    {
        var cached = _cached;
        km2 = cached?.Km2;
        return cached is { } c && time.GetUtcNow() < c.Expires;
    }

    // Repaired like the drawn polygon, so a city boundary with a defect still counts. Empty or zero-area boundaries do not.
    private async Task<double?> MeasureLargestCityAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT max(area_km2)
            FROM (
                SELECT {GapMeasure.SquareKmSql("repaired.geometry")} AS area_km2
                FROM "Administrative"."Cities" c
                CROSS JOIN LATERAL (SELECT ST_CollectionExtract(ST_MakeValid(c."Geometry"), 3) AS geometry) repaired
                WHERE c."Geometry" IS NOT NULL AND NOT ST_IsEmpty(repaired.geometry)
            ) measured
            WHERE area_km2 > 0
            """);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (double)result;
    }
}
