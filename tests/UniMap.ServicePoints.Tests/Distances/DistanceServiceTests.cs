using NetTopologySuite.Geometries;
using Npgsql;
using UniMap.ServicePoints.API.Distances;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Distances;

/// <summary>A fake HERE that records every driving-distance request and can be made to fail.</summary>
internal sealed class FakeDistanceHere(double km = 12.5) : IHereRoutingClient
{
    private readonly List<(Coordinate From, Coordinate To)> _calls = [];

    public bool Fail { get; set; }

    public IReadOnlyList<(Coordinate From, Coordinate To)> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    public Task<DrivingRoute> GetDrivingRouteAsync(Coordinate from, Coordinate to, CancellationToken ct) => throw new NotSupportedException();
    public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct)
    {
        lock (_calls) _calls.Add((from, to));
        return Fail ? throw new HereUnavailableException("HERE is down.") : Task.FromResult(km);
    }

    public Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
        Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct) =>
        throw new NotSupportedException();
}

public class DistanceServiceTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        await _seed.Point(1, "A", 33.0, 39.0);
        await _seed.Point(2, "B", 33.1, 39.1);
        var b = new NpgsqlDataSourceBuilder(db.ConnectionString);
        b.UseNetTopologySuite();
        _ds = b.Build();
    }

    public async Task DisposeAsync() => await _ds.DisposeAsync();

    private DistanceService Service(IHereRoutingClient here, DistanceCache? cache = null) =>
        new(_ds, here, cache ?? new DistanceCache());

    [Fact]
    public async Task First_call_asks_HERE_with_the_stored_coordinates_and_returns_the_distance()
    {
        var here = new FakeDistanceHere(12.5);

        var km = await Service(here).GetDrivingDistanceKmAsync(1, 2, CancellationToken.None);

        Assert.Equal(12.5, km);
        var call = Assert.Single(here.Calls);
        Assert.Equal((33.0, 39.0), (call.From.X, call.From.Y));
        Assert.Equal((33.1, 39.1), (call.To.X, call.To.Y));
    }

    [Fact]
    public async Task Second_call_for_the_same_pair_in_reverse_order_is_served_from_the_cache()
    {
        var here = new FakeDistanceHere(12.5);
        var service = Service(here);

        await service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None);
        var reversed = await service.GetDrivingDistanceKmAsync(2, 1, CancellationToken.None);

        Assert.Equal(12.5, reversed);
        Assert.Single(here.Calls);
    }

    [Fact]
    public async Task Moving_a_point_makes_the_next_call_ask_HERE_again()
    {
        var here = new FakeDistanceHere();
        var service = Service(here);
        await service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None);

        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(33.5, 39.5), 4326) WHERE "Id" = 2""");
        await service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None);

        Assert.Equal(2, here.Calls.Count);
        Assert.Equal(33.5, here.Calls[1].To.X);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("null-active")]
    [InlineData("no-geometry")]
    [InlineData("empty")]
    [InlineData("longitude-out-of-range")]
    [InlineData("latitude-out-of-range")]
    public async Task A_point_that_is_not_active_and_valid_returns_null_without_calling_HERE(string problem)
    {
        switch (problem)
        {
            case "inactive": await _seed.Point(3, "C", active: false); break;
            case "null-active": await _seed.Point(3, "C", active: null); break;
            case "empty": await _seed.Point(3, "C"); await _seed.SetGeometry(3, "POINT EMPTY"); break;
            case "longitude-out-of-range": await _seed.Point(3, "C", x: 200); break;
            case "latitude-out-of-range": await _seed.Point(3, "C", y: 91); break;
            default: await _seed.Point(3, "C", hasGeometry: false); break;
        }
        var here = new FakeDistanceHere();

        Assert.Null(await Service(here).GetDrivingDistanceKmAsync(1, 3, CancellationToken.None));
        Assert.Null(await Service(here).GetDrivingDistanceKmAsync(3, 1, CancellationToken.None));
        Assert.Empty(here.Calls);
    }

    [Fact]
    public async Task An_unknown_point_returns_null_without_calling_HERE()
    {
        var here = new FakeDistanceHere();

        Assert.Null(await Service(here).GetDrivingDistanceKmAsync(1, 999, CancellationToken.None));
        Assert.Empty(here.Calls);
    }

    [Fact]
    public async Task The_same_point_twice_is_zero_without_calling_HERE()
    {
        var here = new FakeDistanceHere();

        Assert.Equal(0, await Service(here).GetDrivingDistanceKmAsync(1, 1, CancellationToken.None));
        Assert.Empty(here.Calls);
    }

    [Fact]
    public async Task HERE_is_always_asked_from_the_lower_id_to_the_higher_id()
    {
        var here = new FakeDistanceHere();

        await Service(here).GetDrivingDistanceKmAsync(2, 1, CancellationToken.None);

        var call = Assert.Single(here.Calls);
        Assert.Equal((33.0, 39.0), (call.From.X, call.From.Y)); // point 1
        Assert.Equal((33.1, 39.1), (call.To.X, call.To.Y));     // point 2
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public async Task A_non_finite_or_negative_distance_is_a_HERE_failure_and_is_not_cached(double bad)
    {
        var here = new FakeDistanceHere(bad);
        var service = Service(here);

        await Assert.ThrowsAsync<HereUnavailableException>(() => service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None));
        await Assert.ThrowsAsync<HereUnavailableException>(() => service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None));
        Assert.Equal(2, here.Calls.Count);
    }

    [Fact]
    public async Task A_HERE_failure_propagates_and_is_not_cached()
    {
        var here = new FakeDistanceHere(7) { Fail = true };
        var service = Service(here);

        await Assert.ThrowsAsync<HereUnavailableException>(() => service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None));

        here.Fail = false;
        Assert.Equal(7, await service.GetDrivingDistanceKmAsync(1, 2, CancellationToken.None));
        Assert.Equal(2, here.Calls.Count);
    }
}
