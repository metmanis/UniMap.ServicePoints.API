using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Npgsql;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Isochrones;
using UniMap.ServicePoints.Tests.Here;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Isochrones;

public sealed class IsochroneServiceTests : IAsyncLifetime
{
    private static readonly GeometryFactory Gf = new(new PrecisionModel(), 4326);

    private readonly ScratchDatabase _db = new();
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var b = new NpgsqlDataSourceBuilder(_db.ConnectionString);
        b.UseNetTopologySuite();
        _ds = b.Build();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static MultiPolygon Square(double size) => Gf.CreateMultiPolygon([Gf.CreatePolygon(
    [
        new Coordinate(0, 0), new Coordinate(size, 0), new Coordinate(size, size), new Coordinate(0, size), new Coordinate(0, 0),
    ])]);

    private sealed class FakeHere(Func<Coordinate, int, Task<MultiPolygon>>? answer = null) : IHereRoutingClient
    {
        private int _calls;
        private int _inFlight;
        private int _maxInFlight;
        public int Calls => _calls;
        public int MaxInFlight => _maxInFlight;
        public List<CancellationToken> Tokens { get; } = [];
        public List<(VehicleType, RangeType, int)> Requests { get; } = [];

        public async Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
            Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Tokens) { Tokens.Add(ct); Requests.Add((vehicle, rangeType, rangeValues[0])); }
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight)) Interlocked.CompareExchange(ref _maxInFlight, now, seen);
            try
            {
                await Task.Yield();
                var g = answer is null ? Square(origin.X + 1) : await answer(origin, rangeValues[0]);
                return [new Isoline(rangeValues[0], g)];
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private IsochroneService Service(IHereRoutingClient here, ILoggerProvider? logs = null) =>
        new(_ds, here, LoggerFactory.Create(b => { if (logs is not null) b.AddProvider(logs); }).CreateLogger<IsochroneService>());

    private async Task Exec(string sql)
    {
        await using var c = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task AddPoint(int id, bool stale = true)
    {
        await Exec($"""INSERT INTO "Services"."ServicePoints" ("Id", "Type", "Geometry") VALUES ({id}, 'Authorized', ST_SetSRID(ST_MakePoint({id}, 0), 4326))""");
        // Updating only IsStale fires no trigger; inserts may mark stale on their own, so set it explicitly.
        await Exec($"""UPDATE "Services"."ServicePoints" SET "IsStale" = {stale} WHERE "Id" = {id}""");
    }

    private Task<bool> IsStale(int id) => Scalar<bool>($"""SELECT "IsStale" FROM "Services"."ServicePoints" WHERE "Id" = {id}""");

    private Task<long> Rows(string where = "true") =>
        Scalar<long>($"""SELECT count(*) FROM "Services"."ServicePointIsochrones" WHERE {where}""");

    private static ServicePointCoverageTarget Target(int id) => new(id, new Coordinate(id, 0));

    [Fact]
    public async Task Miss_calls_here_once_stores_a_row_with_computed_at_and_returns_the_geometry()
    {
        await AddPoint(1);
        var here = new FakeHere();

        var result = await Service(here).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 1800, default);

        Assert.Equal(1, here.Calls);
        var r = Assert.Single(result);
        Assert.Equal(new ServicePointIsochrone(1, RangeType.Time, 1800, r.Geometry), r);
        Assert.True(Square(2).EqualsExact(r.Geometry));
        Assert.Equal(1L, await Rows($"\"ServicePointId\" = 1 AND \"VehicleType\" = 'car' AND \"RangeType\" = 'time' AND \"RangeValue\" = 1800 AND \"ComputedAt\" > now() - interval '1 minute'"));
    }

    [Fact]
    public async Task Hit_returns_the_stored_geometry_without_calling_here()
    {
        await AddPoint(1);
        var here = new FakeHere();
        var svc = Service(here);
        await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 1800, default);

        var second = await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 1800, default);

        Assert.Equal(1, here.Calls);
        Assert.True(Square(2).EqualsExact(Assert.Single(second).Geometry));
    }

    [Fact]
    public async Task A_smaller_range_after_a_larger_one_is_a_miss_with_its_own_row()
    {
        await AddPoint(1);
        var here = new FakeHere();
        var svc = Service(here);
        await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 1800, default);

        await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 1200, default);

        Assert.Equal(2, here.Calls);
        Assert.Equal(2L, await Rows("\"ServicePointId\" = 1"));
    }

    [Theory]
    [InlineData(VehicleType.PassengerCar, RangeType.Time, "car", "time", 600)]
    [InlineData(VehicleType.HeavyDuty, RangeType.Time, "truck", "time", 600)]
    [InlineData(VehicleType.PassengerCar, RangeType.Distance, "car", "distance", 25000)]
    [InlineData(VehicleType.HeavyDuty, RangeType.Distance, "truck", "distance", 25000)]
    public async Task Every_vehicle_and_range_type_is_stored_with_database_text_and_hits_on_repeat(
        VehicleType vehicle, RangeType rangeType, string vehicleText, string rangeText, int value)
    {
        await AddPoint(1);
        var here = new FakeHere();
        var svc = Service(here);

        var first = await svc.GetOrComputeAsync([Target(1)], vehicle, rangeType, value, default);
        var again = await svc.GetOrComputeAsync([Target(1)], vehicle, rangeType, value, default);

        Assert.Equal(1, here.Calls);
        Assert.Equal((vehicle, rangeType, value), here.Requests.Single());
        Assert.Equal(1L, await Rows($"\"VehicleType\" = '{vehicleText}' AND \"RangeType\" = '{rangeText}' AND \"RangeValue\" = {value}"));
        Assert.Equal(rangeType, Assert.Single(again).RangeType);
        Assert.Equal(value, Assert.Single(first).RangeValue);
    }

    [Fact]
    public async Task Stored_and_returned_geometry_has_srid_4326_even_when_here_gives_none()
    {
        await AddPoint(1);
        var noSrid = new FakeHere((_, _) => Task.FromResult(new GeometryFactory().CreateMultiPolygon(
            [new GeometryFactory().CreatePolygon([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0)])])));
        var svc = Service(noSrid);

        var computed = await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        var cached = await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);

        Assert.Equal(4326, await Scalar<int>("""SELECT ST_SRID("Geometry") FROM "Services"."ServicePointIsochrones" """));
        Assert.Equal(4326, computed[0].Geometry.SRID);
        Assert.Equal(4326, cached[0].Geometry.SRID);
    }

    [Fact]
    public async Task Success_clears_is_stale_for_computed_and_cached_points_but_not_for_others()
    {
        await AddPoint(1);
        await AddPoint(2);
        await AddPoint(3);
        var svc = Service(new FakeHere());
        await svc.GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        await Exec("""UPDATE "Services"."ServicePoints" SET "IsStale" = true WHERE "Id" = 1""");

        await svc.GetOrComputeAsync([Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default);

        Assert.False(await IsStale(1));
        Assert.False(await IsStale(2));
        Assert.True(await IsStale(3));
    }

    [Fact]
    public async Task Here_failure_writes_nothing_for_that_point_keeps_its_stale_flag_and_propagates()
    {
        await AddPoint(1);
        await AddPoint(2);
        var here = new FakeHere((o, _) => o.X == 2
            ? throw new HereUnavailableException("boom")
            : Task.FromResult(Square(1)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            Service(here).GetOrComputeAsync([Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default));

        Assert.Equal(0L, await Rows("\"ServicePointId\" = 2"));
        Assert.True(await IsStale(2));
    }

    [Fact]
    public async Task Cache_hit_points_stay_stale_when_another_point_fails()
    {
        await AddPoint(1);
        await AddPoint(2);
        await Service(new FakeHere()).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        await Exec("""UPDATE "Services"."ServicePoints" SET "IsStale" = true WHERE "Id" = 1""");
        var failing = new FakeHere((_, _) => throw new HereUnavailableException("boom"));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            Service(failing).GetOrComputeAsync([Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default));

        Assert.True(await IsStale(1));
    }

    [Fact]
    public async Task Here_is_called_only_for_missing_points_at_most_eight_at_a_time_with_the_callers_token()
    {
        for (var i = 1; i <= 20; i++) await AddPoint(i);
        var svc0 = Service(new FakeHere());
        await svc0.GetOrComputeAsync([Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        var here = new FakeHere(async (o, _) => { await Task.Delay(20); return Square(1); });
        using var cts = new CancellationTokenSource();

        var result = await Service(here).GetOrComputeAsync(
            Enumerable.Range(1, 20).Select(Target).ToList(), VehicleType.PassengerCar, RangeType.Time, 600, cts.Token);

        Assert.Equal(20, result.Count);
        Assert.Equal(Enumerable.Range(1, 20), result.Select(r => r.ServicePointId));
        Assert.Equal(18, here.Calls);
        Assert.InRange(here.MaxInFlight, 2, IsochroneService.MaxConcurrentHereCalls);
        Assert.All(here.Tokens, t => Assert.Equal(cts.Token, t));
        Assert.Equal(8, IsochroneService.MaxConcurrentHereCalls);
    }

    [Fact]
    public async Task After_a_failure_no_new_here_calls_start_and_the_exception_reaches_the_caller()
    {
        for (var i = 1; i <= 20; i++) await AddPoint(i);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        // The first call to arrive fails, but only once all eight calls are in flight; the other seven stay held.
        var here = new FakeHere(async (_, _) =>
        {
            var n = Interlocked.Increment(ref arrived);
            if (n == IsochroneService.MaxConcurrentHereCalls) allEntered.SetResult();
            if (n == 1)
            {
                await allEntered.Task;
                throw new HereUnavailableException("boom");
            }
            await release.Task;
            return Square(1);
        });

        var run = Service(here).GetOrComputeAsync(
            Enumerable.Range(1, 20).Select(Target).ToList(), VehicleType.PassengerCar, RangeType.Time, 600, default);
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // There is no signal for "the failure was processed", so yield a bounded number of times to give a queued
        // waiter the chance to wrongly start a ninth call before the seven held calls are released.
        for (var i = 0; i < 200; i++) await Task.Yield();
        release.SetResult();

        await Assert.ThrowsAsync<HereUnavailableException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(IsochroneService.MaxConcurrentHereCalls, here.Calls);
    }

    [Fact]
    public async Task Rows_from_points_that_succeeded_before_a_failure_stay_and_only_the_failed_point_is_untouched()
    {
        await AddPoint(1);
        await AddPoint(2);
        var point1Stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var here = new FakeHere(async (o, _) =>
        {
            if (o.X != 2) return Square(1);
            await point1Stored.Task; // fail only after point 1 has committed
            throw new HereUnavailableException("boom");
        });

        var run = Service(here).GetOrComputeAsync(
            [Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await Rows("\"ServicePointId\" = 1") == 0) await Task.Delay(10, deadline.Token);
        point1Stored.SetResult();

        await Assert.ThrowsAsync<HereUnavailableException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1L, await Rows("\"ServicePointId\" = 1"));
        Assert.False(await IsStale(1));
        Assert.Equal(0L, await Rows("\"ServicePointId\" = 2"));
        Assert.True(await IsStale(2));
    }

    [Fact]
    public async Task Concurrent_calculations_return_the_same_stored_geometry()
    {
        await AddPoint(1);
        var gate = new TaskCompletionSource();
        var a = new FakeHere(async (_, _) => { await gate.Task; return Square(3); });
        var b = new FakeHere(async (_, _) => { await gate.Task; return Square(5); });

        var ta = Service(a).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        var tb = Service(b).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);
        while (a.Calls == 0 || b.Calls == 0) await Task.Delay(5);
        gate.SetResult();
        var ra = (await ta)[0];
        var rb = (await tb)[0];

        Assert.Equal(1L, await Rows());
        Assert.True(ra.Geometry.EqualsExact(rb.Geometry));
    }

    private static MultiPolygon Ring(params (double X, double Y)[] points) => Gf.CreateMultiPolygon(
        [Gf.CreatePolygon(points.Select(p => new Coordinate(p.X, p.Y)).ToArray())]);

    public static TheoryData<string, MultiPolygon> NoAreaGeometries() => new()
    {
        { "empty", Gf.CreateMultiPolygon() },
        { "collinear", Ring((0, 0), (1, 1), (2, 2), (0, 0)) },
        { "identical points", Ring((1, 1), (1, 1), (1, 1), (1, 1)) },
    };

    [Theory]
    [MemberData(nameof(NoAreaGeometries))]
    public async Task A_here_answer_with_no_area_is_rejected_and_nothing_is_stored(string _, MultiPolygon geometry)
    {
        await AddPoint(1);
        var here = new FakeHere((_, _) => Task.FromResult(geometry));

        var ex = await Assert.ThrowsAsync<NoReachableAreaException>(() =>
            Service(here).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default));

        Assert.Equal(1, ex.ServicePointId);
        Assert.Equal(0L, await Rows());
        Assert.True(await IsStale(1));
    }

    [Fact]
    public async Task An_empty_isoline_from_here_names_the_service_point()
    {
        await AddPoint(1);
        await AddPoint(2);
        var here = new FakeHere((o, _) => o.X == 2 ? throw new HereEmptyIsolineException("empty") : Task.FromResult(Square(1)));

        var ex = await Assert.ThrowsAsync<NoReachableAreaException>(() =>
            Service(here).GetOrComputeAsync([Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default));

        Assert.Equal(2, ex.ServicePointId);
        Assert.Contains("service point 2", ex.Message);
        Assert.Equal(0L, await Rows());
    }

    private sealed class NoLinesHere : IHereRoutingClient
    {
        public Task<IReadOnlyList<Isoline>> GetIsolinesAsync(Coordinate o, VehicleType v, RangeType r, IReadOnlyList<int> values, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Isoline>>([]);
        public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task No_line_for_the_requested_value_names_the_service_point()
    {
        await AddPoint(1);

        var ex = await Assert.ThrowsAsync<NoReachableAreaException>(() =>
            Service(new NoLinesHere()).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default));

        Assert.Equal(1, ex.ServicePointId);
    }

    private const string MoveToFifty = """UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(50, 0), 4326) WHERE "Id" = """;

    [Fact]
    public async Task A_point_moved_during_the_here_call_stores_no_row_stays_stale_and_is_left_out()
    {
        await AddPoint(1);
        await AddPoint(2);
        var here = new FakeHere(async (o, _) =>
        {
            if (o.X == 1) await Exec(MoveToFifty + "1");
            return Square(1);
        });

        var result = await Service(here).GetOrComputeAsync([Target(1), Target(2)], VehicleType.PassengerCar, RangeType.Time, 600, default);

        Assert.Equal(2, Assert.Single(result).ServicePointId);
        Assert.Equal(0L, await Rows("\"ServicePointId\" = 1"));
        Assert.True(await IsStale(1));
        Assert.Equal(1L, await Rows("\"ServicePointId\" = 2"));
        Assert.False(await IsStale(2));
    }

    [Fact]
    public async Task A_point_moved_and_back_during_the_here_call_is_stored()
    {
        await AddPoint(1);
        var here = new FakeHere(async (_, _) =>
        {
            await Exec(MoveToFifty + "1");
            await Exec("""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(1, 0), 4326) WHERE "Id" = 1""");
            return Square(1);
        });

        Assert.Single(await Service(here).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default));
        Assert.Equal(1L, await Rows());
    }

    [Fact]
    public async Task A_point_deleted_during_the_here_call_is_skipped_without_an_error()
    {
        await AddPoint(1);
        var here = new FakeHere(async (_, _) =>
        {
            await Exec("""DELETE FROM "Services"."ServicePoints" WHERE "Id" = 1""");
            return Square(1);
        });

        Assert.Empty(await Service(here).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default));
        Assert.Equal(0L, await Rows());
    }

    // The move has not committed when the insert runs, so the insert must wait for it. Inserting from the old
    // snapshot would leave a row the move's trigger cannot see and delete.
    [Fact]
    public async Task An_insert_racing_an_uncommitted_move_waits_for_it_and_stores_no_row()
    {
        await AddPoint(1);
        await using var mover = await _db.OpenAsync();
        await using var tx = await mover.BeginTransactionAsync();
        await using (var move = new NpgsqlCommand(MoveToFifty + "1", mover, tx)) await move.ExecuteNonQueryAsync();

        var run = Service(new FakeHere()).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await Scalar<long>("""SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""") == 0)
            await Task.Delay(10, deadline.Token);
        await tx.CommitAsync();

        Assert.Empty(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0L, await Rows());
        Assert.True(await IsStale(1));
    }

    // Two calculations for the same point must not deadlock when both insert and then clear the stale flag.
    [Fact]
    public async Task Many_concurrent_calculations_for_one_point_all_succeed()
    {
        await AddPoint(1);

        var runs = Enumerable.Range(0, 16).Select(_ =>
            Service(new FakeHere()).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default));
        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(results, r => Assert.Single(r));
        Assert.Equal(1L, await Rows());
    }

    [Fact]
    public async Task A_self_intersecting_answer_whose_repair_has_area_is_still_stored()
    {
        await AddPoint(1);
        var bowTie = Ring((0, 0), (2, 2), (2, 0), (0, 2), (0, 0)); // the signed areas of its two lobes cancel out
        var here = new FakeHere((_, _) => Task.FromResult(bowTie));

        await Service(here).GetOrComputeAsync([Target(1)], VehicleType.PassengerCar, RangeType.Time, 600, default);

        Assert.Equal(1L, await Rows());
    }

    [Fact]
    public async Task Empty_points_return_empty_with_no_here_call_and_no_database_access()
    {
        var here = new FakeHere();
        await _ds.DisposeAsync(); // any database use would now throw

        var result = await Service(here).GetOrComputeAsync([], VehicleType.PassengerCar, RangeType.Time, 600, default);

        Assert.Empty(result);
        Assert.Equal(0, here.Calls);
        var b = new NpgsqlDataSourceBuilder(_db.ConnectionString);
        b.UseNetTopologySuite();
        _ds = b.Build();
    }

    [Fact]
    public async Task Logs_point_vehicle_range_type_and_value_on_success_and_failure_and_never_a_key_or_url()
    {
        await AddPoint(1);
        await AddPoint(2);
        var logs = new CapturingLoggerProvider();
        var here = new FakeHere((o, _) => o.X == 2
            ? throw new HereUnavailableException("rejected")
            : Task.FromResult(Square(1)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            Service(here, logs).GetOrComputeAsync([Target(1), Target(2)], VehicleType.HeavyDuty, RangeType.Distance, 7000, default));

        Assert.Contains(logs.Lines, l => l.Contains("Information") && l.Contains('1') && l.Contains("HeavyDuty") && l.Contains("Distance") && l.Contains("7000"));
        Assert.Contains(logs.Lines, l => l.Contains("Warning") && l.Contains('2') && l.Contains("HeavyDuty") && l.Contains("Distance") && l.Contains("7000"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("apiKey", StringComparison.OrdinalIgnoreCase) || l.Contains("http", StringComparison.OrdinalIgnoreCase));
    }
}

public class IsochroneRegistrationTests
{
    private static ServiceProvider Provider(string? connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(connectionString is null ? [] : [new("ConnectionStrings:ServicePoints", connectionString)])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHereRoutingClient, NoHere>();
        services.AddIsochrones();
        return services.BuildServiceProvider();
    }

    private sealed class NoHere : IHereRoutingClient
    {
        public Task<IReadOnlyList<Isoline>> GetIsolinesAsync(Coordinate o, VehicleType v, RangeType r, IReadOnlyList<int> values, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Service_is_registered_and_resolves_with_a_connection_string()
    {
        using var provider = Provider("Host=localhost;Database=unimap_sp_test_unused;Username=u;Password=p");
        using var scope = provider.CreateScope();

        Assert.IsType<IsochroneService>(scope.ServiceProvider.GetRequiredService<IIsochroneService>());
    }

    [Fact]
    public void Provider_builds_without_a_connection_string_and_resolving_fails_with_a_clear_message()
    {
        using var provider = Provider(null);
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IIsochroneService>());
        Assert.Contains("ConnectionStrings:ServicePoints", ex.Message);
    }
}
