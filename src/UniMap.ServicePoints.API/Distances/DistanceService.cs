using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using Npgsql;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.ServicePoints;

namespace UniMap.ServicePoints.API.Distances;

/// <summary>
/// Ordered pair of ids (lower first) with both points' coordinates as stored now. A point that moves therefore
/// gets a new key, so a cached distance can never be stale and nothing needs clearing.
/// </summary>
public readonly record struct DistanceCacheKey(int LowId, double LowX, double LowY, int HighId, double HighX, double HighY);

/// <summary>Successful driving distances, per process. Old entries leave when the size limit is reached.</summary>
public sealed class DistanceCache : IDisposable
{
    public const int MaxEntries = 10_000;

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public bool TryGet(DistanceCacheKey key, out double km) => _cache.TryGetValue(key, out km);

    public void Set(DistanceCacheKey key, double km) =>
        _cache.Set(key, km, new MemoryCacheEntryOptions { Size = 1 });

    public void Dispose() => _cache.Dispose();
}

/// <summary>Driving distance between two service points, cars only.</summary>
public class DistanceService(NpgsqlDataSource dataSource, IHereRoutingClient here, DistanceCache cache)
{
    /// <returns>Kilometres, or null when either point is unknown, inactive or has no valid location.</returns>
    /// <exception cref="HereUnavailableException">HERE failed. Nothing is cached and no distance is returned.</exception>
    public async Task<double?> GetDrivingDistanceKmAsync(int fromServicePointId, int toServicePointId, CancellationToken ct)
    {
        var from = await ReadLocationAsync(fromServicePointId, ct);
        if (from is null) return null;
        if (fromServicePointId == toServicePointId) return 0;
        var to = await ReadLocationAsync(toServicePointId, ct);
        if (to is null) return null;

        // The pair is always asked lowest id first, so the cached figure never depends on which direction came first.
        var (low, high) = fromServicePointId < toServicePointId ? (from, to) : (to, from);
        var key = new DistanceCacheKey(
            Math.Min(fromServicePointId, toServicePointId), low.X, low.Y,
            Math.Max(fromServicePointId, toServicePointId), high.X, high.Y);
        if (cache.TryGet(key, out var cached)) return cached;

        var km = await here.GetDrivingDistanceKmAsync(low, high, ct);
        if (!double.IsFinite(km) || km < 0)
            throw new HereUnavailableException("HERE returned a distance that is not a finite, non-negative number.");
        cache.Set(key, km);
        return km;
    }

    private async Task<Coordinate?> ReadLocationAsync(int id, CancellationToken ct)
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
