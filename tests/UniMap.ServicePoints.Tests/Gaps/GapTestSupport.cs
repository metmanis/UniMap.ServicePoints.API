using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.Tests.Gaps;

internal static class GapShapes
{
    public static readonly GeometryFactory Gf = new(new PrecisionModel(), 4326);

    public static Polygon RectPolygon(double x1, double y1, double x2, double y2) => Poly(
        (x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1));

    public static MultiPolygon Rect(double x1, double y1, double x2, double y2) => Multi(RectPolygon(x1, y1, x2, y2));

    /// <summary>Builds a polygon from longitude, latitude pairs. The ring is taken as given, so it may be invalid.</summary>
    public static Polygon Poly(params (double X, double Y)[] ring) =>
        Gf.CreatePolygon(ring.Select(p => new Coordinate(p.X, p.Y)).ToArray());

    public static MultiPolygon Multi(params Polygon[] polygons) => Gf.CreateMultiPolygon(polygons);

    public static void AssertSameShape(Geometry expected, Geometry actual)
    {
        var difference = expected.SymmetricDifference(actual).Area;
        Assert.True(difference < 1e-9, $"Shapes differ by {difference} square degrees. Expected {expected}, got {actual}.");
    }

    public static void AssertCovers(Geometry gap, double x, double y) =>
        Assert.True(gap.Covers(Gf.CreatePoint(new Coordinate(x, y))), $"Expected ({x}, {y}) to be a gap.");

    public static void AssertNotCovers(Geometry gap, double x, double y) =>
        Assert.False(gap.Covers(Gf.CreatePoint(new Coordinate(x, y))), $"Expected ({x}, {y}) not to be a gap.");
}

/// <summary>A fake HERE that answers per origin and range, and records every isoline request.</summary>
internal sealed class ScriptedHere(Func<Coordinate, RangeType, int, Task<MultiPolygon>> answer) : IHereRoutingClient
{
    private readonly List<(Coordinate Origin, VehicleType Vehicle, RangeType Range, int Value)> _requests = [];

    public ScriptedHere(Func<Coordinate, RangeType, int, MultiPolygon> answer)
        : this((o, r, v) => Task.FromResult(answer(o, r, v)))
    {
    }

    public IReadOnlyList<(Coordinate Origin, VehicleType Vehicle, RangeType Range, int Value)> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public async Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
        Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct)
    {
        lock (_requests) _requests.Add((origin, vehicle, rangeType, rangeValues[0]));
        return [new Isoline(rangeValues[0], await answer(origin, rangeType, rangeValues[0]))];
    }

    public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A clock that only moves when a test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
