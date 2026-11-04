using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Isochrones;
using UniMap.ServicePoints.Tests.Here;
using UniMap.ServicePoints.Tests.Support;
using static UniMap.ServicePoints.Tests.Gaps.GapShapes;

namespace UniMap.ServicePoints.Tests.Gaps;

/// <summary>Runs the real GapService and IsochroneService against PostGIS. Only the HERE client is fake.</summary>
public class GapServiceTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    // About 17 km wide and 22 km tall at latitude 39. One degree of longitude is about 86.5 km here.
    private const double West = 33.0, South = 39.0, East = 33.2, North = 39.2;
    private static readonly Polygon Area = RectPolygon(West, South, East, North);
    private const double FarX = 40;

    private readonly ServicePointSeed _seed = new(db);
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        var b = new NpgsqlDataSourceBuilder(db.ConnectionString);
        b.UseNetTopologySuite();
        _ds = b.Build();
    }

    public async Task DisposeAsync() => await _ds.DisposeAsync();

    private GapService Gaps(
        IHereRoutingClient here, GapCache? cache = null, double maxSpeedKmh = 130, ILoggerProvider? logs = null, AreaLimit? areaLimit = null)
    {
        var loggers = LoggerFactory.Create(b => { if (logs is not null) b.AddProvider(logs); });
        return new GapService(
            _ds,
            new IsochroneService(_ds, here, NullLogger<IsochroneService>.Instance),
            cache ?? new GapCache(),
            areaLimit ?? new AreaLimit(_ds, TimeProvider.System, NullLogger<AreaLimit>.Instance),
            Options.Create(new ServicePointLimits(5, 60, 5, 100)),
            Options.Create(new GapOptions { MaxSpeedKmh = maxSpeedKmh }),
            loggers.CreateLogger<GapService>());
    }

    private static GapRequest Request(int? minutes, int? km, VehicleType vehicle = VehicleType.PassengerCar, Polygon? area = null) =>
        new(area ?? Area, vehicle, minutes, km);

    private async Task AddPoint(int id, double x, double y = 39.1, string? vehicle = "PassengerCar", bool? active = true)
    {
        await _seed.Point(id, $"P{id}", x, y, active: active);
        if (vehicle is not null) await _seed.VehicleType(id, vehicle);
    }

    /// <summary>A point far from every test area, so the data counts as sufficient while no candidate is near.</summary>
    private Task AddFarPoint(int id = 900, string vehicle = "PassengerCar") => AddPoint(id, FarX, vehicle: vehicle);

    private async Task SeedRow(int id, string vehicle, string range, int value, MultiPolygon geometry)
    {
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO "Services"."ServicePointIsochrones"
                ("ServicePointId", "VehicleType", "RangeType", "RangeValue", "Geometry", "ComputedAt")
            VALUES (@id, @vehicle, @range, @value, @geometry, now())
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("vehicle", vehicle);
        cmd.Parameters.AddWithValue("range", range);
        cmd.Parameters.AddWithValue("value", value);
        cmd.Parameters.Add(new NpgsqlParameter("geometry", NpgsqlDbType.Geometry) { Value = geometry });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<double> RealAreaKm2(Geometry geometry)
    {
        await using var cmd = _ds.CreateCommand("SELECT ST_Area(@g::geography) / 1000000.0");
        cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Geometry) { Value = geometry });
        return (double)(await cmd.ExecuteScalarAsync())!;
    }

    private static MultiPolygon Everything() => Rect(32, 38, 34, 40);

    /// <summary>A north-south strip 0.1 degrees wide centred on the origin, the same for every range.</summary>
    private static MultiPolygon StripAround(Coordinate origin) => Rect(origin.X - 0.05, 38.9, origin.X + 0.05, 39.3);

    private static ScriptedHere StripHere() => new((o, _, _) => StripAround(o));

    private static ScriptedHere NoHere() => new(MultiPolygon (Coordinate _, RangeType _, int _) =>
        throw new InvalidOperationException("HERE must not be called in this test."));

    private static MultiPolygon GapsOf(GapResult result)
    {
        Assert.True(result.IsCalculated);
        Assert.NotNull(result.Gaps);
        Assert.Equal(4326, result.Gaps!.SRID);
        return result.Gaps;
    }

    // (a)
    [Fact]
    public async Task Covered_by_time_only_distance_only_or_both_is_not_a_gap_and_covered_by_neither_is()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, r, _) => r == RangeType.Time
            ? Rect(32.9, 38.9, 33.10, 39.3)
            : Rect(33.05, 38.9, 33.15, 39.3));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, 50), default));

        AssertNotCovers(gap, 33.02, 39.1); // time only
        AssertNotCovers(gap, 33.08, 39.1); // both
        AssertNotCovers(gap, 33.12, 39.1); // distance only
        AssertCovers(gap, 33.18, 39.1); // neither
        AssertSameShape(Rect(33.15, South, East, North), gap);
    }

    // (b)
    [Fact]
    public async Task An_area_with_no_service_points_near_it_is_a_full_gap_and_no_error()
    {
        await AddFarPoint();
        var here = NoHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, 50), default));

        AssertSameShape(Area, gap);
        Assert.Empty(here.Requests);
    }

    [Fact]
    public async Task A_full_gap_smaller_than_one_km2_is_dropped_as_a_sliver()
    {
        await AddFarPoint();
        var tiny = RectPolygon(33.0, 39.0, 33.005, 39.005); // about 0.43 km x 0.55 km

        var gap = GapsOf(await Gaps(NoHere()).CalculateAsync(Request(30, null, area: tiny), default));

        Assert.True(gap.IsEmpty);
    }

    // (c)
    [Fact]
    public async Task A_self_intersecting_bow_tie_returns_a_result()
    {
        await AddPoint(1, 33.05);
        var bowTie = Poly((West, South), (East, North), (East, South), (West, North), (West, South));
        Assert.False(bowTie.IsValid);
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3)); // the left half

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null, area: bowTie), default));

        var rightLobe = Poly((33.1, 39.1), (East, South), (East, North), (33.1, 39.1));
        AssertSameShape(rightLobe, gap);
    }

    // (d)
    [Fact]
    public async Task A_piece_of_0_9_km2_at_latitude_39_is_dropped_and_a_piece_of_1_1_km2_is_kept()
    {
        await AddPoint(1, 33.05);
        var small1 = await LShapeOf(33.02, 39.04, 0.9);
        var kept = await LShapeOf(33.08, 39.04, 1.1);
        var small2 = await LShapeOf(33.14, 39.04, 0.9);
        Assert.InRange(await RealAreaKm2(small1), 0.89, 0.91);
        Assert.InRange(await RealAreaKm2(kept), 1.09, 1.11);
        Assert.InRange(await RealAreaKm2(small2), 0.89, 0.91);

        var reach = Gf.CreatePolygon(
            Gf.CreateLinearRing(RectPolygon(32.9, 38.9, 33.3, 39.3).Coordinates),
            new[] { small1, kept, small2 }.Select(h => Gf.CreateLinearRing(h.Coordinates)).ToArray());
        var here = new ScriptedHere((_, _, _) => Multi(reach));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        var piece = Assert.Single(gap.Geometries);
        AssertSameShape(kept, piece);
        Assert.InRange(await RealAreaKm2(piece), 1.09, 1.11);
    }

    /// <summary>An L shape of the given real area, scaled by its measured area at this latitude.</summary>
    private async Task<Polygon> LShapeOf(double x, double y, double targetKm2)
    {
        const double height = 0.02;
        Polygon Shape(double w) => Poly(
            (x, y), (x + w, y), (x + w, y + height / 2), (x + w / 2, y + height / 2), (x + w / 2, y + height), (x, y + height), (x, y));

        const double trial = 0.01;
        return Shape(trial * targetKm2 / await RealAreaKm2(Shape(trial)));
    }

    // (e)
    [Fact]
    public async Task A_heavy_duty_calculation_ignores_a_passenger_car_only_point()
    {
        await AddPoint(1, 33.05, vehicle: "PassengerCar");
        await AddPoint(2, 33.15, vehicle: "HeavyDuty");
        var here = StripHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null, VehicleType.HeavyDuty), default));

        var request = Assert.Single(here.Requests);
        Assert.Equal(33.15, request.Origin.X);
        Assert.Equal(VehicleType.HeavyDuty, request.Vehicle);
        AssertSameShape(Rect(West, South, 33.1, North), gap);
    }

    // (f)
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_inactive_point_is_ignored_even_if_a_stored_isochrone_covers_the_area(bool? active)
    {
        await AddFarPoint();
        await AddPoint(1, 33.1, active: active);
        await SeedRow(1, "car", "time", 1800, Everything());
        var here = NoHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area, gap);
    }

    [Fact]
    public async Task A_point_without_the_vehicle_link_is_ignored_even_if_a_stored_isochrone_covers_the_area()
    {
        await AddFarPoint();
        await AddPoint(1, 33.1, vehicle: "HeavyDuty");
        await SeedRow(1, "car", "time", 1800, Everything());
        var here = NoHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area, gap);
    }

    // (g)
    [Fact]
    public async Task No_point_for_the_vehicle_gives_not_enough_spatial_data_and_no_here_call()
    {
        await AddPoint(1, 33.1, vehicle: "PassengerCar");
        var here = NoHere();

        var result = await Gaps(here).CalculateAsync(Request(30, 50, VehicleType.HeavyDuty), default);

        Assert.False(result.IsCalculated);
        Assert.Equal("Not enough spatial data", result.Message);
        Assert.Null(result.Gaps);
        Assert.Empty(here.Requests);
    }

    [Fact]
    public async Task Inactive_points_and_points_without_geometry_are_not_sufficient_data()
    {
        await AddPoint(1, 33.1, active: false);
        await _seed.Point(2, "no geometry", hasGeometry: false);
        await _seed.VehicleType(2, "PassengerCar");

        var result = await Gaps(NoHere()).CalculateAsync(Request(30, null), default);

        Assert.False(result.IsCalculated);
    }

    [Theory]
    [InlineData("POINT EMPTY")]
    [InlineData("POINT(200 39.1)")]
    [InlineData("POINT(-181 39.1)")]
    [InlineData("POINT(33.1 91)")]
    [InlineData("POINT(33.1 -91)")]
    public async Task A_point_without_a_valid_location_is_not_sufficient_data_and_is_never_a_candidate(string wkt)
    {
        await AddPoint(1, 33.1);
        await _seed.SetGeometry(1, wkt);
        var here = NoHere();

        var result = await Gaps(here).CalculateAsync(Request(30, 50), default);

        Assert.False(result.IsCalculated);
        Assert.Empty(here.Requests);
    }

    [Fact]
    public async Task Many_out_of_range_points_among_valid_ones_never_turn_a_calculation_into_a_geography_error()
    {
        await AddPoint(1, 33.05);
        for (var id = 100; id < 400; id++)
        {
            await AddPoint(id, 33.1);
            await _seed.SetGeometry(id, id % 3 == 0 ? "POINT(200 39.1)" : id % 3 == 1 ? "POINT(33.1 95)" : "POINT(-400 -100)");
        }
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, 50), default));

        Assert.All(here.Requests, r => Assert.Equal(33.05, r.Origin.X));
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    [Fact]
    public async Task A_point_without_a_valid_location_does_not_stop_the_calculation_for_the_others()
    {
        await AddPoint(1, 33.05);
        await AddPoint(2, 33.1);
        await _seed.SetGeometry(2, "POINT(200 39.1)");
        await AddPoint(3, 33.15);
        await _seed.SetGeometry(3, "POINT EMPTY");
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.Equal(33.05, Assert.Single(here.Requests).Origin.X);
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    // (h)
    [Fact]
    public async Task A_drive_time_with_no_distance_unions_only_time_isochrones()
    {
        await AddPoint(1, 33.05);
        await SeedRow(1, "car", "distance", 50000, Everything());
        var here = StripHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.Equal(RangeType.Time, Assert.Single(here.Requests).Range);
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    [Fact]
    public async Task A_distance_with_no_drive_time_unions_only_distance_isochrones()
    {
        await AddPoint(1, 33.05);
        await SeedRow(1, "car", "time", 1800, Everything());
        var here = StripHere();

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(null, 50), default));

        Assert.Equal(RangeType.Distance, Assert.Single(here.Requests).Range);
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    // (i)
    [Fact]
    public async Task The_gap_follows_the_drivable_reach_and_never_a_circle_around_the_point()
    {
        await AddPoint(1, 33.055);
        // A road corridor with a branch. A location 1.3 km from the point, off the corridor, stays a gap.
        var reach = Poly((33.05, 38.9), (33.06, 38.9), (33.06, 39.1), (33.16, 39.13), (33.16, 39.15), (33.05, 39.12), (33.05, 38.9));
        var here = new ScriptedHere((_, _, _) => Multi(reach));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area.Difference(reach), gap);
        AssertCovers(gap, 33.07, 39.1);
        AssertNotCovers(gap, 33.055, 39.1);
    }

    // Decision 1
    [Fact]
    public async Task A_point_outside_the_polygon_that_reaches_into_it_covers_that_part()
    {
        await AddPoint(1, 32.95, y: 39.1);
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        var request = Assert.Single(here.Requests);
        Assert.Equal(32.95, request.Origin.X); // longitude
        Assert.Equal(39.1, request.Origin.Y); // latitude
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    [Theory]
    [InlineData(null, 20)] // radius 20 km
    [InlineData(10, null)] // radius 10 minutes x 130 km/h = 21.7 km
    public async Task A_point_beyond_the_radius_causes_no_here_call(int? minutes, int? km)
    {
        await AddPoint(1, 33.4); // 17.3 km east of the area
        await AddPoint(2, 33.5); // 26 km east of the area
        var here = StripHere();

        await Gaps(here).CalculateAsync(Request(minutes, km), default);

        Assert.Equal(33.4, Assert.Single(here.Requests).Origin.X);
    }

    [Fact]
    public async Task The_time_radius_uses_the_configured_top_speed()
    {
        await AddPoint(1, 33.25); // 4.3 km east of the area
        await AddPoint(2, 33.4); // 17.3 km east of the area
        var here = StripHere();

        await Gaps(here, maxSpeedKmh: 60).CalculateAsync(Request(10, null), default); // radius 10 km

        Assert.Equal(33.25, Assert.Single(here.Requests).Origin.X);
    }

    [Fact]
    public async Task Each_range_type_chooses_its_own_candidates_and_the_candidate_counts_are_logged()
    {
        await AddPoint(1, 33.1);
        await AddPoint(2, 33.4); // 17.3 km away: inside the 21.7 km time radius, outside the 5 km distance radius
        var here = StripHere();
        var logs = new CapturingLoggerProvider();

        await Gaps(here, logs: logs).CalculateAsync(Request(10, 5), default);

        var byRange = here.Requests.GroupBy(r => r.Range).ToDictionary(g => g.Key, g => g.Select(r => r.Origin.X).Order().ToArray());
        Assert.Equal([33.1, 33.4], byRange[RangeType.Time]);
        Assert.Equal([33.1], byRange[RangeType.Distance]);
        Assert.Contains(logs.Lines, l => l.Contains("2 candidates") && l.Contains("Time"));
        Assert.Contains(logs.Lines, l => l.Contains("1 candidates") && l.Contains("Distance"));
    }

    [Fact]
    public async Task A_candidate_whose_reach_misses_the_polygon_changes_nothing()
    {
        await AddPoint(1, 33.3);
        var here = new ScriptedHere((_, _, _) => Rect(33.25, 39.0, 33.35, 39.2));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.Single(here.Requests);
        AssertSameShape(Area, gap);
    }

    // Decision 2
    public static TheoryData<string, Polygon?> BadAreas() => new()
    {
        { "null", null },
        { "empty", Gf.CreatePolygon() },
        { "zero area", Poly((33.0, 39.0), (33.1, 39.1), (33.2, 39.2), (33.0, 39.0)) },
        { "longitude out of range", RectPolygon(179.5, 39.0, 180.5, 39.2) },
        { "latitude out of range", RectPolygon(33.0, 89.5, 33.2, 90.5) },
        { "not finite", Poly((33.0, 39.0), (33.1, double.PositiveInfinity), (33.2, 39.0), (33.0, 39.0)) },
    };

    [Theory]
    [MemberData(nameof(BadAreas))]
    public async Task A_bad_area_is_a_validation_error_even_with_no_points_at_all(string _, Polygon? area)
    {
        var request = new GapRequest(area!, VehicleType.PassengerCar, 30, null);

        var ex = await Assert.ThrowsAsync<GapValidationException>(() => Gaps(NoHere()).CalculateAsync(request, default));

        Assert.NotEmpty(ex.Errors);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(4, null)]
    [InlineData(null, 101)]
    public async Task Bad_thresholds_are_a_validation_error(int? minutes, int? km)
    {
        var ex = await Assert.ThrowsAsync<GapValidationException>(() => Gaps(NoHere()).CalculateAsync(Request(minutes, km), default));

        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public async Task A_stored_self_intersecting_isochrone_is_repaired_before_the_union()
    {
        await AddPoint(1, 33.1);
        var bowTie = Poly((32.9, 38.9), (33.3, 39.3), (33.3, 38.9), (32.9, 39.3), (32.9, 38.9));
        var here = new ScriptedHere((_, _, _) => Multi(bowTie));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        var left = Poly((32.9, 38.9), (33.1, 39.1), (32.9, 39.3), (32.9, 38.9));
        var right = Poly((33.3, 38.9), (33.3, 39.3), (33.1, 39.1), (33.3, 38.9));
        AssertSameShape(Area.Difference(left).Difference(right), gap);
    }

    [Fact]
    public async Task Polygon_srid_is_forced_to_4326()
    {
        await AddFarPoint();
        var area = (Polygon)Area.Copy();
        area.SRID = 0;

        var gap = GapsOf(await Gaps(NoHere()).CalculateAsync(Request(30, null, area: area), default));

        AssertSameShape(Area, gap);
    }

    // Decision 3
    [Fact]
    public async Task Full_coverage_is_calculated_with_an_empty_multipolygon()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Everything());

        var result = await Gaps(here).CalculateAsync(Request(30, null), default);

        Assert.Null(result.Message);
        Assert.True(GapsOf(result).IsEmpty);
    }

    // Decision 4
    [Fact]
    public async Task A_second_identical_request_makes_no_here_call_and_no_gap_recalculation()
    {
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        var first = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, 50), default));
        var calls = here.Requests.Count;
        Assert.Equal(2, calls);

        // The stored geometry changes behind the cache, with ComputedAt untouched. A recalculation would see it.
        await _seed.Exec("""UPDATE "Services"."ServicePointIsochrones" SET "Geometry" = ST_Multi(ST_MakeEnvelope(32, 38, 34, 40, 4326))""");
        var second = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, 50), default));

        Assert.Equal(calls, here.Requests.Count);
        AssertSameShape(first, second);
        Assert.False(second.IsEmpty);
        // Control: without the cached entry the same stored rows do give a different answer.
        Assert.True(GapsOf(await Gaps(here, new GapCache()).CalculateAsync(Request(30, 50), default)).IsEmpty);
    }

    [Fact]
    public async Task A_cached_empty_result_is_still_returned_as_calculated()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Everything());
        var cache = new GapCache();

        await Gaps(here, cache).CalculateAsync(Request(30, null), default);
        var second = await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        Assert.True(GapsOf(second).IsEmpty);
        Assert.Single(here.Requests);
    }

    [Fact]
    public async Task Different_thresholds_or_vehicle_or_area_do_not_share_a_cached_result()
    {
        await AddPoint(1, 33.05, vehicle: "PassengerCar");
        await _seed.VehicleType(1, "HeavyDuty");
        var here = StripHere();
        var cache = new GapCache();

        await Gaps(here, cache).CalculateAsync(Request(30, null), default);
        await Gaps(here, cache).CalculateAsync(Request(31, null), default);
        await Gaps(here, cache).CalculateAsync(Request(null, 30), default);
        await Gaps(here, cache).CalculateAsync(Request(30, null, VehicleType.HeavyDuty), default);
        var otherArea = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null, area: RectPolygon(33.0, 39.0, 33.2, 39.1)), default));

        Assert.Equal(4, here.Requests.Count); // the other area reuses the stored isochrone
        AssertSameShape(Rect(33.1, 39.0, 33.2, 39.1), otherArea);
    }

    [Fact]
    public async Task After_moving_a_point_the_next_request_gives_the_new_result()
    {
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        AssertSameShape(Rect(33.1, South, East, North), GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default)));

        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(33.15, 39.1), 4326) WHERE "Id" = 1""");
        var gap = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));

        AssertSameShape(Rect(West, South, 33.1, North), gap);
    }

    [Fact]
    public async Task After_adding_a_point_the_next_request_gives_the_new_result()
    {
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        Assert.False(GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default)).IsEmpty);

        await AddPoint(2, 33.15);
        var gap = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));

        Assert.True(gap.IsEmpty);
    }

    [Fact]
    public async Task After_deactivating_a_point_the_next_request_gives_the_new_result()
    {
        await AddFarPoint();
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "IsActive" = false WHERE "Id" = 1""");
        var gap = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area, gap);
    }

    [Fact]
    public async Task After_removing_a_vehicle_link_the_next_request_gives_the_new_result()
    {
        await AddFarPoint();
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        await _seed.Exec("""DELETE FROM "Services"."ServicePointVehicleTypes" WHERE "ServicePointId" = 1""");
        var gap = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area, gap);
    }

    [Fact]
    public async Task A_lost_isochrone_row_is_fetched_again_and_changes_the_fingerprint()
    {
        await AddPoint(1, 33.05);
        var here = StripHere();
        var cache = new GapCache();
        await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        await _seed.Exec("""DELETE FROM "Services"."ServicePointIsochrones" """);
        await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        Assert.Equal(2, here.Requests.Count);
    }

    [Fact]
    public async Task Insufficient_data_is_not_cached_and_is_checked_before_the_cache()
    {
        var here = StripHere();
        var cache = new GapCache();
        Assert.False((await Gaps(here, cache).CalculateAsync(Request(30, null), default)).IsCalculated);

        await AddFarPoint();
        var calculated = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));
        AssertSameShape(Area, calculated);

        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "IsActive" = false""");
        Assert.False((await Gaps(here, cache).CalculateAsync(Request(30, null), default)).IsCalculated);
    }

    [Fact]
    public async Task A_point_added_during_the_fetch_is_fetched_too_so_it_cannot_cause_a_false_gap()
    {
        await AddPoint(1, 33.05);
        var added = false;
        var here = new ScriptedHere(async (o, _, _) =>
        {
            if (!added)
            {
                added = true;
                await AddPoint(2, 33.15);
            }
            return StripAround(o);
        });

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.True(gap.IsEmpty);
        Assert.Equal([33.05, 33.15], here.Requests.Select(r => r.Origin.X).Order());
    }

    [Fact]
    public async Task Rows_that_keep_going_missing_fail_the_calculation_instead_of_returning_a_false_gap()
    {
        await AddPoint(1, 33.05);
        var nextId = 2;
        var here = new ScriptedHere(async (o, _, _) =>
        {
            await AddPoint(Interlocked.Increment(ref nextId), 33.15);
            return StripAround(o);
        });

        await Assert.ThrowsAsync<GapCalculationUnstableException>(() => Gaps(here).CalculateAsync(Request(30, null), default));
    }

    // Final review: a reach stored for the old location would hide a real gap, or report a false one.
    [Fact]
    public async Task A_point_moved_during_the_here_call_is_refetched_at_its_new_location()
    {
        await AddPoint(1, 33.05);
        var moved = false;
        var here = new ScriptedHere(async (o, _, _) =>
        {
            if (!moved)
            {
                moved = true;
                await _seed.SetGeometry(1, "POINT(33.15 39.1)");
            }
            return StripAround(o);
        });

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.Equal([33.05, 33.15], here.Requests.Select(r => r.Origin.X));
        AssertSameShape(Rect(West, South, 33.1, North), gap);
        Assert.Equal(1L, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointIsochrones" """));
    }

    [Fact]
    public async Task A_point_that_keeps_moving_ends_in_the_unstable_failure_and_no_false_gap()
    {
        await AddPoint(1, 33.05);
        var step = 0;
        var here = new ScriptedHere(async (o, _, _) =>
        {
            var x = (33.06 + Interlocked.Increment(ref step) * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await _seed.SetGeometry(1, $"POINT({x} 39.1)");
            return StripAround(o);
        });

        await Assert.ThrowsAsync<GapCalculationUnstableException>(() => Gaps(here).CalculateAsync(Request(30, null), default));

        Assert.Equal(GapService.MaxCalculationAttempts, here.Requests.Count);
        Assert.Equal(0L, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointIsochrones" """));
    }

    // Final review: points that cannot be located are disclosed, because a gap may contain one of them.
    [Fact]
    public async Task The_excluded_count_is_active_points_of_the_vehicle_with_no_valid_location_nationwide()
    {
        await AddPoint(1, 33.05);                                        // located: not counted
        await _seed.Point(2, "No geometry", hasGeometry: false);
        await _seed.VehicleType(2, "PassengerCar");                      // counted
        await AddPoint(3, FarX);
        await _seed.SetGeometry(3, "POINT(999 99)");                     // out of range: counted
        await _seed.Point(4, "Inactive", active: false, hasGeometry: false);
        await _seed.VehicleType(4, "PassengerCar");                      // inactive: not counted
        await _seed.Point(5, "Unknown active", active: null, hasGeometry: false);
        await _seed.VehicleType(5, "PassengerCar");                      // null is not active: not counted
        await _seed.Point(6, "Heavy only", hasGeometry: false);
        await _seed.VehicleType(6, "HeavyDuty");                         // other vehicle: not counted
        await _seed.Point(7, "No vehicle", hasGeometry: false);          // no link: not counted

        var result = await Gaps(StripHere()).CalculateAsync(Request(30, null), default);

        Assert.Equal(2, result.ExcludedPointCount);
    }

    [Fact]
    public async Task The_excluded_count_follows_the_chosen_vehicle()
    {
        await AddPoint(1, 33.05, vehicle: "HeavyDuty");
        await _seed.Point(6, "Heavy only", hasGeometry: false);
        await _seed.VehicleType(6, "HeavyDuty");

        var result = await Gaps(StripHere()).CalculateAsync(Request(30, null, VehicleType.HeavyDuty), default);

        Assert.Equal(1, result.ExcludedPointCount);
    }

    [Fact]
    public async Task The_excluded_count_is_read_fresh_on_a_cached_result_and_is_reported_with_no_gaps()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Everything());
        var cache = new GapCache();
        var first = await Gaps(here, cache).CalculateAsync(Request(30, null), default);
        Assert.Equal(0, first.ExcludedPointCount);
        Assert.True(GapsOf(first).IsEmpty);

        await _seed.Point(2, "No geometry", hasGeometry: false);
        await _seed.VehicleType(2, "PassengerCar");
        var second = await Gaps(here, cache).CalculateAsync(Request(30, null), default);

        Assert.Single(here.Requests); // served from the gap cache
        Assert.Equal(1, second.ExcludedPointCount);
        Assert.True(GapsOf(second).IsEmpty);
    }

    // Decision 6
    [Fact]
    public async Task A_here_failure_for_one_point_fails_the_whole_calculation_and_is_not_cached()
    {
        await AddPoint(1, 33.05);
        await AddPoint(2, 33.15);
        var fail = true;
        var here = new ScriptedHere((o, _, _) => fail && o.X == 33.15
            ? throw new HereUnavailableException("rejected")
            : StripAround(o));
        var cache = new GapCache();

        await Assert.ThrowsAsync<HereUnavailableException>(() => Gaps(here, cache).CalculateAsync(Request(30, null), default));

        fail = false;
        Assert.True(GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default)).IsEmpty);
    }

    [Fact]
    public async Task A_here_failure_on_the_second_range_type_also_gives_no_result()
    {
        await AddPoint(1, 33.05);
        var here = new ScriptedHere((o, r, _) => r == RangeType.Distance
            ? throw new HereUnavailableException("rejected")
            : StripAround(o));

        await Assert.ThrowsAsync<HereUnavailableException>(() => Gaps(here).CalculateAsync(Request(30, 50), default));
    }

    // Fix round 1: a reach with no area must never read as reaching nothing.
    public static TheoryData<string, MultiPolygon> NoAreaReaches() => new()
    {
        { "empty", Gf.CreateMultiPolygon() },
        { "collinear", Multi(Poly((33.0, 39.0), (33.5, 39.5), (34.0, 40.0), (33.0, 39.0))) },
        { "collinear with inexact decimals", Multi(Poly((33.1, 39.1), (33.2, 39.2), (33.3, 39.3), (33.7, 39.7), (33.1, 39.1))) },
        { "identical points", Multi(Poly((33.1, 39.1), (33.1, 39.1), (33.1, 39.1), (33.1, 39.1))) },
    };

    [Theory]
    [MemberData(nameof(NoAreaReaches))]
    public async Task A_here_answer_with_no_area_fails_the_calculation_stores_nothing_and_caches_nothing(string _, MultiPolygon reach)
    {
        await AddPoint(1, 33.05);
        var here = new ScriptedHere((_, _, _) => reach);
        var cache = new GapCache();

        await Assert.ThrowsAsync<NoReachableAreaException>(() => Gaps(here, cache).CalculateAsync(Request(30, null), default));
        await Assert.ThrowsAsync<NoReachableAreaException>(() => Gaps(here, cache).CalculateAsync(Request(30, null), default));

        Assert.Equal(2, here.Requests.Count);
        Assert.Equal(0L, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointIsochrones" """));
    }

    [Theory]
    [MemberData(nameof(NoAreaReaches))]
    public async Task A_stored_row_with_no_area_fails_the_calculation_and_caches_nothing(string _, MultiPolygon row)
    {
        await AddPoint(1, 33.05);
        await SeedRow(1, "car", "time", 1800, row);
        var here = StripHere();
        var cache = new GapCache();

        // The fingerprint is the same both times, so a cached result would come back on the second call.
        await Assert.ThrowsAsync<NoReachableAreaException>(() => Gaps(here, cache).CalculateAsync(Request(30, null), default));
        await Assert.ThrowsAsync<NoReachableAreaException>(() => Gaps(here, cache).CalculateAsync(Request(30, null), default));
        Assert.Empty(here.Requests);

        await _seed.Exec("""DELETE FROM "Services"."ServicePointIsochrones" """);
        var gap = GapsOf(await Gaps(here, cache).CalculateAsync(Request(30, null), default));
        AssertSameShape(Rect(33.1, South, East, North), gap);
    }

    [Fact]
    public async Task A_stored_row_with_no_area_for_a_point_that_is_not_a_candidate_changes_nothing()
    {
        await AddFarPoint();
        await AddPoint(1, 33.05, active: false);
        await SeedRow(1, "car", "time", 1800, Gf.CreateMultiPolygon());

        var gap = GapsOf(await Gaps(NoHere()).CalculateAsync(Request(30, null), default));

        AssertSameShape(Area, gap);
    }

    // Fix round 1: polygon edges are straight in longitude and latitude, and a bare geography cast bends them.
    [Fact]
    public async Task A_point_just_inside_the_southern_edge_of_a_wide_area_is_a_candidate()
    {
        var wide = RectPolygon(27, 39, 39, 41); // about 1,040 km wide
        await AddPoint(1, 33, y: 39.09); // 10 km inside the edge at mid-span, 7 km outside a geodesic version of it
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 39.0, 33.1, 39.2));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(null, 5, area: wide), default));

        Assert.Equal(33, Assert.Single(here.Requests).Origin.X);
        AssertNotCovers(gap, 33, 39.09);
        AssertSameShape(wide.Difference(RectPolygon(32.9, 39.0, 33.1, 39.2)), gap);
    }

    [Theory]
    [InlineData(0.9, false)]
    [InlineData(1.1, true)]
    public async Task A_long_thin_piece_is_measured_along_its_drawn_edges(double targetKm2, bool kept)
    {
        const double x1 = 33, x2 = 35; // about 170 km
        var wide = RectPolygon(x1, 39, x2, 39.5);

        const double trial = 0.0001;
        var trialKm2 = await RealAreaKm2(Densified(RectPolygon(x1, 39, x2, 39 + trial)));
        var height = trial * targetKm2 / trialKm2;
        var strip = RectPolygon(x1, 39, x2, 39 + height);

        // The reach follows the top of the strip with many vertices. The bottom is the area's one long edge.
        var ring = new List<(double, double)> { (32.9, 39.6) };
        for (var x = 32.9; x < 35.1; x += 0.002) ring.Add((x, 39 + height));
        ring.Add((35.1, 39 + height));
        ring.Add((35.1, 39.6));
        ring.Add((32.9, 39.6));
        await AddPoint(1, 34, y: 39.25);
        var here = new ScriptedHere((_, _, _) => Multi(Poly(ring.ToArray())));

        var gap = GapsOf(await Gaps(here).CalculateAsync(Request(30, null, area: wide), default));

        if (kept)
        {
            AssertSameShape(strip, gap);
            Assert.InRange(await RealAreaKm2(Densified(gap)), targetKm2 - 0.01, targetKm2 + 0.01);
        }
        else
        {
            Assert.True(gap.IsEmpty, $"Expected the {targetKm2} km2 strip to be dropped, got {gap.NumGeometries} pieces.");
        }
    }

    /// <summary>Vertices every 0.001 degrees, so the measured geography follows the planar edges.</summary>
    private static Geometry Densified(Geometry geometry) =>
        NetTopologySuite.Densify.Densifier.Densify(geometry, 0.001);

    // Decision 8
    [Fact]
    public async Task Thresholds_reach_the_isochrone_layer_as_exact_seconds_and_metres()
    {
        await AddPoint(1, 33.05);
        var here = StripHere();

        await Gaps(here).CalculateAsync(Request(37, 53), default);

        Assert.Equal(37 * 60, here.Requests.Single(r => r.Range == RangeType.Time).Value);
        Assert.Equal(53 * 1000, here.Requests.Single(r => r.Range == RangeType.Distance).Value);
        Assert.Equal(1L, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointIsochrones" WHERE "VehicleType" = 'car' AND "RangeType" = 'time' AND "RangeValue" = 2220"""));
        Assert.Equal(1L, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointIsochrones" WHERE "VehicleType" = 'car' AND "RangeType" = 'distance' AND "RangeValue" = 53000"""));
    }

    // Area limit: a drawn area may not be larger than the largest city.

    private async Task<double> MeasuredKm2(Geometry geometry)
    {
        await using var cmd = _ds.CreateCommand($"SELECT {GapMeasure.SquareKmSql("@g")}");
        cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Geometry) { Value = geometry });
        return (double)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Km2(double km2) => km2.ToString("N1", System.Globalization.CultureInfo.InvariantCulture);

    private async Task SeedCities()
    {
        await _seed.City(1, "Small", 33.0, 39.0, 33.1, 39.1);
        await _seed.City(2, "Big", 33.0, 39.0, 33.4, 39.4);
        await AddFarPoint();
    }

    [Fact]
    public async Task An_area_larger_than_the_largest_city_is_rejected_with_both_areas_and_no_HERE_call()
    {
        await SeedCities();
        var big = await MeasuredKm2(RectPolygon(33.0, 39.0, 33.4, 39.4));
        var drawn = RectPolygon(33.0, 39.0, 33.41, 39.41);
        var here = NoHere();

        var ex = await Assert.ThrowsAsync<GapValidationException>(
            () => Gaps(here).CalculateAsync(Request(30, 50, area: drawn), default));

        var message = Assert.Single(ex.Errors);
        Assert.Contains(Km2(await MeasuredKm2(drawn)) + " km", message);
        Assert.Contains(Km2(big) + " km", message);
        Assert.Empty(here.Requests);
    }

    [Fact]
    public async Task An_area_just_under_the_largest_city_passes_and_the_limit_is_the_biggest_not_the_first()
    {
        await SeedCities();

        var gap = GapsOf(await Gaps(NoHere()).CalculateAsync(Request(30, 50, area: RectPolygon(33.0, 39.0, 33.399, 39.399)), default));

        Assert.False(gap.IsEmpty);
    }

    [Fact]
    public async Task An_area_exactly_as_large_as_the_largest_city_passes()
    {
        await SeedCities();

        GapsOf(await Gaps(NoHere()).CalculateAsync(Request(30, 50, area: RectPolygon(33.0, 39.0, 33.4, 39.4)), default));
    }

    [Fact]
    public async Task A_self_intersecting_area_is_repaired_before_it_is_measured()
    {
        await SeedCities();
        // Repaired, this bow tie is two triangles with 0.18 square degrees between them. The big city is 0.16.
        var bowTie = Poly((33.0, 39.0), (33.6, 39.6), (33.6, 39.0), (33.0, 39.6), (33.0, 39.0));
        Assert.False(bowTie.IsValid);

        await Assert.ThrowsAsync<GapValidationException>(
            () => Gaps(NoHere()).CalculateAsync(Request(30, 50, area: bowTie), default));
    }

    [Fact]
    public async Task With_no_usable_city_the_limit_is_not_enforced_and_a_warning_is_logged()
    {
        await _seed.City(1, "Ghost", 33.0, 39.0, 33.1, 39.1);
        await _seed.Exec("""UPDATE "Administrative"."Cities" SET "Geometry" = NULL""");
        await AddFarPoint();
        var logs = new CapturingLoggerProvider();
        var limit = new AreaLimit(_ds, TimeProvider.System, LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<AreaLimit>());

        var gap = GapsOf(await Gaps(NoHere(), areaLimit: limit).CalculateAsync(Request(30, 50, area: RectPolygon(30, 30, 39, 45)), default));

        Assert.False(gap.IsEmpty);
        Assert.Null(await limit.GetMaxAreaKm2Async(default));
        Assert.Contains(logs.Lines, l => l.Contains("Warning") && l.Contains("Administrative.Cities"));
    }

    [Fact]
    public async Task The_largest_city_area_is_cached_for_one_hour()
    {
        await _seed.City(1, "Small", 33.0, 39.0, 33.1, 39.1);
        var clock = new ManualTimeProvider();
        var limit = new AreaLimit(_ds, clock, NullLogger<AreaLimit>.Instance);
        var small = await MeasuredKm2(RectPolygon(33.0, 39.0, 33.1, 39.1));
        Assert.Equal(small, await limit.GetMaxAreaKm2Async(default));

        await _seed.City(2, "Big", 33.0, 39.0, 33.4, 39.4);
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(small, await limit.GetMaxAreaKm2Async(default));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(await MeasuredKm2(RectPolygon(33.0, 39.0, 33.4, 39.4)), await limit.GetMaxAreaKm2Async(default));
    }

    [Fact]
    public async Task A_city_with_an_invalid_boundary_is_still_measured_after_repair()
    {
        await _seed.City(1, "Small", 33.0, 39.0, 33.1, 39.1);
        await _seed.Exec("""
            INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry")
            VALUES (2, 'Bowtie', ST_Multi(ST_GeomFromText('POLYGON((33 39, 33.4 39.4, 33.4 39, 33 39.4, 33 39))', 4326)))
            """);

        var max = await new AreaLimit(_ds, TimeProvider.System, NullLogger<AreaLimit>.Instance).GetMaxAreaKm2Async(default);

        Assert.True(max > await MeasuredKm2(RectPolygon(33.0, 39.0, 33.1, 39.1)));
    }
}

public class GapOptionsTests
{
    private static ServiceProvider Provider(string? maxSpeed)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(maxSpeed is null ? [] : [new("Gaps:MaxSpeedKmh", maxSpeed)])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddGaps();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_top_speed_is_130()
    {
        using var provider = Provider(null);

        Assert.Equal(130, provider.GetRequiredService<IOptions<GapOptions>>().Value.MaxSpeedKmh);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_top_speed_that_is_not_positive_fails_at_startup_and_names_the_key(string value)
    {
        using var provider = Provider(value);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Gaps:MaxSpeedKmh", ex.Message);
    }
}
