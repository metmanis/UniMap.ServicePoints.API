using NetTopologySuite.Geometries;
using Npgsql;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Routes;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Routes;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A fake HERE that counts route requests, can fail, and can hold every request until a barrier is met.</summary>
internal sealed class FakeRouteHere : IHereRoutingClient
{
    private int _calls;

    public int Calls => _calls;
    public Exception? Fail { get; set; }
    public double Meters { get; set; } = 1500.5;
    public int Seconds { get; set; } = 120;
    public Barrier? Barrier { get; set; }

    public async Task<DrivingRoute> GetDrivingRouteAsync(Coordinate from, Coordinate to, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Barrier is { } barrier) await Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(10), ct), ct);
        if (Fail is not null) throw Fail;
        var line = new LineString([from, new Coordinate((from.X + to.X) / 2, from.Y + 0.01), to]) { SRID = 4326 };
        return new DrivingRoute(line, Meters, Seconds);
    }

    public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
        Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct) =>
        throw new NotSupportedException();
}

public class RouteServiceTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 30, 15, TimeSpan.Zero);

    private readonly ServicePointSeed _seed = new(db);
    private readonly ManualTimeProvider _time = new(Start);
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

    private RouteService Service(IHereRoutingClient here) => new(_ds, here, _time);

    private Task<long> RowCount() => _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointRoutes" """);

    [Fact]
    public async Task No_saved_route_asks_HERE_and_saves_it()
    {
        var here = new FakeRouteHere();

        var route = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        Assert.NotNull(route);
        Assert.False(route.FromCache);
        Assert.Equal(Start, route.CalculatedAt);
        Assert.Equal(1500.5, route.DistanceMeters);
        Assert.Equal(120, route.DurationSeconds);
        Assert.Equal(1, here.Calls);
        Assert.Equal(1, await RowCount());
    }

    [Fact]
    public async Task Saved_route_is_returned_without_calling_HERE()
    {
        var here = new FakeRouteHere();
        var first = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);
        _time.Now = Start.AddDays(30);

        var second = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        Assert.NotNull(second);
        Assert.True(second.FromCache);
        Assert.Equal(1, here.Calls);
        Assert.Equal(first!.Geometry.Coordinates, second.Geometry.Coordinates);
        Assert.Equal(first.DistanceMeters, second.DistanceMeters);
        Assert.Equal(first.DurationSeconds, second.DurationSeconds);
        Assert.Equal(first.CalculatedAt, second.CalculatedAt);
    }

    [Fact]
    public async Task A_route_exactly_6_months_old_is_used()
    {
        var here = new FakeRouteHere();
        await Service(here).GetRouteAsync(1, 2, CancellationToken.None);
        _time.Now = Start.AddMonths(RouteService.ValidMonths);

        var route = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        Assert.True(route!.FromCache);
        Assert.Equal(1, here.Calls);
    }

    [Fact]
    public async Task A_route_older_than_6_months_asks_HERE_and_replaces_the_row()
    {
        var here = new FakeRouteHere();
        await Service(here).GetRouteAsync(1, 2, CancellationToken.None);
        _time.Now = Start.AddMonths(RouteService.ValidMonths).AddSeconds(1);
        here.Meters = 2000;
        here.Seconds = 300;

        var route = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        Assert.False(route!.FromCache);
        Assert.Equal(2, here.Calls);
        Assert.Equal(1, await RowCount());
        var again = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);
        Assert.True(again!.FromCache);
        Assert.Equal(2000, again.DistanceMeters);
        Assert.Equal(300, again.DurationSeconds);
        Assert.Equal(_time.Now, again.CalculatedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_moved_point_asks_HERE_again(int movedPoint)
    {
        var here = new FakeRouteHere();
        await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        await _seed.Exec($"""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(33.5, 39.5), 4326) WHERE "Id" = {movedPoint}""");
        var route = await Service(here).GetRouteAsync(1, 2, CancellationToken.None);

        Assert.False(route!.FromCache);
        Assert.Equal(2, here.Calls);
        Assert.Equal(1, await RowCount());
        Assert.True((await Service(here).GetRouteAsync(1, 2, CancellationToken.None))!.FromCache);
    }

    [Fact]
    public async Task Each_direction_is_stored_separately()
    {
        var here = new FakeRouteHere();
        var service = Service(here);

        await service.GetRouteAsync(1, 2, CancellationToken.None);
        await service.GetRouteAsync(2, 1, CancellationToken.None);

        Assert.Equal(2, here.Calls);
        Assert.Equal(2, await RowCount());
    }

    [Fact]
    public async Task Nothing_is_saved_when_HERE_fails()
    {
        var here = new FakeRouteHere { Fail = new HereUnavailableException("HERE is down.") };

        await Assert.ThrowsAsync<HereUnavailableException>(() => Service(here).GetRouteAsync(1, 2, CancellationToken.None));

        Assert.Equal(0, await RowCount());
    }

    [Fact]
    public async Task An_expired_route_is_not_returned_when_HERE_fails()
    {
        var here = new FakeRouteHere();
        await Service(here).GetRouteAsync(1, 2, CancellationToken.None);
        _time.Now = Start.AddMonths(RouteService.ValidMonths).AddSeconds(1);
        here.Fail = new HereNoRouteException("No route.");

        await Assert.ThrowsAsync<HereNoRouteException>(() => Service(here).GetRouteAsync(1, 2, CancellationToken.None));

        Assert.Equal(1, await RowCount());
        Assert.Equal(Start.UtcDateTime, await _seed.Scalar<DateTime>("""SELECT "CalculatedAt" FROM "Services"."ServicePointRoutes" """));
        Assert.Equal(1500.5, await _seed.Scalar<double>("""SELECT "DistanceMeters" FROM "Services"."ServicePointRoutes" """));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("inactive")]
    [InlineData("null-active")]
    [InlineData("no-geometry")]
    [InlineData("empty")]
    [InlineData("longitude-out-of-range")]
    public async Task Unknown_inactive_or_unlocated_point_returns_null_without_calling_HERE(string problem)
    {
        switch (problem)
        {
            case "unknown": break;
            case "inactive": await _seed.Point(3, "C", active: false); break;
            case "null-active": await _seed.Point(3, "C", active: null); break;
            case "empty": await _seed.Point(3, "C"); await _seed.SetGeometry(3, "POINT EMPTY"); break;
            case "longitude-out-of-range": await _seed.Point(3, "C", x: 200); break;
            default: await _seed.Point(3, "C", hasGeometry: false); break;
        }
        var here = new FakeRouteHere();

        Assert.Null(await Service(here).GetRouteAsync(1, 3, CancellationToken.None));
        Assert.Null(await Service(here).GetRouteAsync(3, 1, CancellationToken.None));
        Assert.Equal(0, here.Calls);
        Assert.Equal(0, await RowCount());
    }

    [Fact]
    public async Task Simultaneous_requests_for_a_new_route_both_succeed()
    {
        // Both requests find no saved route and reach HERE together before either saves.
        var here = new FakeRouteHere { Barrier = new Barrier(2) };

        var results = await Task.WhenAll(
            Service(here).GetRouteAsync(1, 2, CancellationToken.None),
            Service(here).GetRouteAsync(1, 2, CancellationToken.None));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(2, here.Calls);
        Assert.Equal(1, await RowCount());
    }
}
