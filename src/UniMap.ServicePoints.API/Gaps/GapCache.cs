using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Gaps;

/// <param name="Fingerprint">
/// Hash of the candidate point ids and the ComputedAt of their stored rows. Any change to the points changes
/// it, so an entry can never be stale and nothing needs clearing. Old entries leave when the size limit is reached.
/// </param>
public readonly record struct GapCacheKey(
    string AreaHash, VehicleType Vehicle, int? DriveTimeSeconds, int? DistanceMetres, string Fingerprint);

/// <summary>Successful gap results, per process.</summary>
public sealed class GapCache : IDisposable
{
    public const int MaxEntries = 256;

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public bool TryGet(GapCacheKey key, out MultiPolygon? gaps)
    {
        if (!_cache.TryGetValue(key, out MultiPolygon? stored))
        {
            gaps = null;
            return false;
        }

        gaps = Isolated(stored!); // geometries are mutable, and a caller must not be able to change the cached one
        return true;
    }

    public void Set(GapCacheKey key, MultiPolygon gaps) =>
        _cache.Set(key, Isolated(gaps), new MemoryCacheEntryOptions { Size = 1 });

    public void Dispose() => _cache.Dispose();

    private static MultiPolygon Isolated(MultiPolygon gaps) => (MultiPolygon)gaps.Copy();
}
