using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.Tests.Here;

public class HereRoutingClientTests
{
    private const string FakeKey = "FAKE-KEY-do-not-leak-0123456789";

    // NetTopologySuite: X = longitude, Y = latitude. Ankara.
    private static readonly Coordinate Ankara = new(32.85, 39.92);

    private static (HereRoutingClient Client, RecordingDelay Delay) Create(
        FakeHereHandler handler, string? apiKey = FakeKey)
    {
        var delay = new RecordingDelay();
        var client = new HereRoutingClient(
            new HttpClient(handler),
            Options.Create(new HereOptions { ApiKey = apiKey }),
            delay,
            NullLogger<HereRoutingClient>.Instance);
        return (client, delay);
    }

    private static FakeHereHandler Ok(string fixture) =>
        new((_, _) => FakeHereHandler.Json(Fixtures.Read(fixture)));

    // (a)
    [Theory]
    [InlineData(VehicleType.HeavyDuty, "truck", true)]
    [InlineData(VehicleType.PassengerCar, "car", false)]
    public async Task Vehicle_selects_the_HERE_profile_and_vehicle_block(VehicleType vehicle, string mode, bool hasVehicleBlock)
    {
        var handler = Ok(vehicle == VehicleType.HeavyDuty ? "isoline-truck-time.json" : "isoline-car-time.json");
        var (client, _) = Create(handler);

        await client.GetIsolinesAsync(Ankara, vehicle, RangeType.Time, [300, 600], default);

        var query = Assert.Single(handler.Requests).DecodedQuery;
        Assert.Contains($"transportMode={mode}", query);
        Assert.Equal(hasVehicleBlock, query.Contains("vehicle[grossWeight]=12000&vehicle[height]=350&vehicle[width]=250&vehicle[length]=900"));
        Assert.Equal(hasVehicleBlock, query.Contains("vehicle["));
    }

    // (b)
    [Fact]
    public async Task Time_and_distance_are_two_separate_calls()
    {
        var handler = new FakeHereHandler((_, r) => FakeHereHandler.Json(Fixtures.Read(
            r.RequestUri!.Query.Contains("distance") ? "isoline-car-distance.json" : "isoline-car-time.json")));
        var (client, _) = Create(handler);

        await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 600], default);
        await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Distance, [2000, 5000], default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("range[type]=time&range[values]=300,600", handler.Requests[0].DecodedQuery);
        Assert.Contains("range[type]=distance&range[values]=2000,5000", handler.Requests[1].DecodedQuery);
    }

    // (c)
    [Fact]
    public async Task A_503_followed_by_a_200_returns_the_200_result_after_one_second_backoff()
    {
        var handler = new FakeHereHandler((n, _) => n == 1
            ? FakeHereHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
            : FakeHereHandler.Json(Fixtures.Read("isoline-car-time.json")));
        var (client, delay) = Create(handler);

        var result = await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 600], default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, result.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delay.Delays);
    }

    // (d)
    [Fact]
    public async Task A_429_on_every_attempt_makes_four_attempts_with_backoff_then_throws()
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json("{}", HttpStatusCode.TooManyRequests));
        var (client, delay) = Create(handler);

        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delay.Delays);
        Assert.DoesNotContain(FakeKey, ex.Message);
    }

    [Fact]
    public async Task Network_failures_are_retried_like_5xx()
    {
        var handler = new FakeHereHandler((n, _) => n < 3
            ? throw new HttpRequestException("connection reset")
            : FakeHereHandler.Json(Fixtures.Read("isoline-car-time.json")));
        var (client, delay) = Create(handler);

        var result = await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 600], default);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task A_timeout_is_retried_like_5xx_and_ends_in_HereUnavailableException()
    {
        var handler = new FakeHereHandler((_, _) => throw new TaskCanceledException("timed out"));
        var (client, _) = Create(handler);

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHereHandler((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        var (client, _) = Create(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], cts.Token));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Other_4xx_does_not_retry_and_throws_without_the_key(HttpStatusCode status)
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json("{}", status));
        var (client, delay) = Create(handler);

        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.Single(handler.Requests);
        Assert.Empty(delay.Delays);
        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain("apiKey", ex.Message);
    }

    // Decision 4
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"isolines":[{"range":{"type":"time","value":300}}]}""")]
    [InlineData("""{"isolines":[{"range":{"type":"time","value":300},"polygons":[{"outer":""}]}]}""")]
    [InlineData("""{"isolines":[{"range":{"type":"time","value":300},"polygons":[{"outer":"BF!!"}]}]}""")]
    [InlineData("not json")]
    public async Task A_200_without_usable_isolines_throws_instead_of_looking_like_no_reach(string body)
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json(body));
        var (client, _) = Create(handler);

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));
    }

    [Theory]
    [InlineData("""{"isolines":[]}""")]
    [InlineData("""{"isolines":[{"range":{"type":"time","value":300},"polygons":[]}]}""")]
    public async Task A_200_with_an_empty_isoline_is_a_distinct_failure_and_is_not_retried(string body)
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json(body));
        var (client, delay) = Create(handler);

        var ex = await Assert.ThrowsAsync<HereEmptyIsolineException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.Single(handler.Requests);
        Assert.Empty(delay.Delays);
        Assert.DoesNotContain(FakeKey, ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("""{"isolines":[5]}""")]
    [InlineData("""{"isolines":[{"range":5,"polygons":[]}]}""")]
    [InlineData("""{"isolines":[{"range":{"value":"300"},"polygons":[]}]}""")]
    [InlineData("""{"isolines":[{"range":{"value":300},"polygons":[5]}]}""")]
    [InlineData("""{"isolines":[{"range":{"value":300},"polygons":[{"outer":5}]}]}""")]
    public async Task A_malformed_200_body_throws_HereUnavailableException_without_the_key_or_url(string body)
    {
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain("http", ex.Message);
    }

    [Theory]
    [InlineData("[5]")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public async Task A_malformed_inner_ring_throws_and_is_never_read_as_no_holes(string inner)
    {
        var outer = EncodeRing([(0, 0), (0, 10), (10, 10), (10, 0)]);
        var body = $$"""{"isolines":[{"range":{"value":300},"polygons":[{"outer":"{{outer}}","inner":{{inner}}}]}]}""";
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"matrix":5}""")]
    [InlineData("""{"matrix":{"errorCodes":[1.5],"distances":[1]}}""")]
    public async Task A_malformed_matrix_body_throws_HereUnavailableException(string body)
    {
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default));
    }

    [Fact]
    public async Task A_header_only_outer_ring_throws_and_is_never_an_empty_polygon()
    {
        var body = $$"""{"isolines":[{"range":{"value":300},"polygons":[{"outer":"{{EncodeRing([])}}"}]}]}""";
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));
    }

    [Fact]
    public async Task A_header_only_inner_ring_throws()
    {
        var outer = EncodeRing([(0, 0), (0, 10), (10, 10), (10, 0)]);
        var body = $$"""{"isolines":[{"range":{"value":300},"polygons":[{"outer":"{{outer}}","inner":["{{EncodeRing([])}}"]}]}]}""";
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));
    }

    [Fact]
    public async Task A_missing_value_for_a_requested_range_throws()
    {
        var (client, _) = Create(Ok("isoline-car-time.json")); // contains 300 and 600 only

        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 900], default));

        Assert.Contains("900", ex.Message);
    }

    // Decision 7
    [Fact]
    public async Task Distance_values_are_sent_as_metres_and_time_values_as_seconds_unchanged()
    {
        var handler = new FakeHereHandler((_, r) => FakeHereHandler.Json(Fixtures.Read(
            r.RequestUri!.Query.Contains("distance") ? "isoline-car-distance.json" : "isoline-car-time.json")));
        var (client, _) = Create(handler);

        await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Distance, [2000, 5000], default);
        await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 600], default);

        Assert.Contains("range[values]=2000,5000", handler.Requests[0].DecodedQuery);
        Assert.Contains("range[values]=300,600", handler.Requests[1].DecodedQuery);
    }

    [Fact]
    public async Task Each_range_value_is_mapped_back_to_its_own_isoline_in_request_order()
    {
        var (client, _) = Create(Ok("isoline-car-time.json"));

        var result = await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [600, 300], default);

        Assert.Equal([600, 300], result.Select(i => i.RangeValue));
        // The 10-minute area is larger than the 5-minute area.
        Assert.True(result[0].Geometry.Area > result[1].Geometry.Area);
    }

    // Decision 8
    [Fact]
    public async Task Origin_is_sent_as_latitude_then_longitude()
    {
        var handler = Ok("isoline-car-time.json");
        var (client, _) = Create(handler);

        await client.GetIsolinesAsync(new Coordinate(32.85, 39.92), VehicleType.PassengerCar, RangeType.Time, [300, 600], default);

        Assert.Contains("origin=39.92,32.85&", handler.Requests[0].DecodedQuery);
    }

    // Decision 1
    [Fact]
    public async Task Geometry_is_a_closed_SRID_4326_multipolygon_with_longitude_as_X()
    {
        var (client, _) = Create(Ok("isoline-car-time.json"));

        var result = await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300, 600], default);

        var five = result.Single(i => i.RangeValue == 300).Geometry;
        Assert.Equal(4326, five.SRID);
        var polygon = Assert.IsType<Polygon>(Assert.Single(five.Geometries));
        Assert.True(polygon.ExteriorRing.IsClosed);
        Assert.Equal(173, polygon.ExteriorRing.NumPoints);
        Assert.Equal(32.817707, polygon.ExteriorRing.GetCoordinateN(0).X, 6); // longitude
        Assert.Equal(39.914703, polygon.ExteriorRing.GetCoordinateN(0).Y, 6); // latitude
        Assert.True(five.IsValid);
        Assert.True(five.Contains(new Point(32.85, 39.92) { SRID = 4326 }));
    }

    [Fact]
    public async Task Truck_and_distance_fixtures_decode_to_valid_areas_around_the_origin()
    {
        foreach (var (fixture, vehicle, type, values) in new (string, VehicleType, RangeType, int[])[]
                 {
                     ("isoline-truck-time.json", VehicleType.HeavyDuty, RangeType.Time, [300, 600]),
                     ("isoline-car-distance.json", VehicleType.PassengerCar, RangeType.Distance, [2000, 5000]),
                 })
        {
            var (client, _) = Create(Ok(fixture));
            var result = await client.GetIsolinesAsync(Ankara, vehicle, type, values, default);
            Assert.All(result, i =>
            {
                Assert.True(i.Geometry.IsValid);
                Assert.InRange(i.Geometry.EnvelopeInternal.MinX, 32.5, 32.9);
                Assert.InRange(i.Geometry.EnvelopeInternal.MaxY, 39.9, 40.1);
            });
        }
    }

    [Fact]
    public async Task Inner_rings_are_kept_as_holes_and_open_rings_are_closed()
    {
        // 4-point (open) square shell, lat/lng order, precision 5, plus a hole.
        var shell = EncodeRing([(0, 0), (0, 10), (10, 10), (10, 0)]);
        var hole = EncodeRing([(4, 4), (4, 6), (6, 6), (6, 4)]);
        var body = JsonSerializer.Serialize(new
        {
            isolines = new[]
            {
                new { range = new { type = "time", value = 300 }, polygons = new[] { new { outer = shell, inner = new[] { hole } } } },
            },
        });
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        var result = await client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default);

        var polygon = Assert.IsType<Polygon>(Assert.Single(result[0].Geometry.Geometries));
        Assert.True(polygon.ExteriorRing.IsClosed);
        Assert.Equal(1, polygon.NumInteriorRings);
        Assert.Equal(100 - 4, polygon.Area, 6);
    }

    // Matrix
    [Fact]
    public async Task Driving_distance_is_returned_in_km_and_the_request_has_the_verified_shape()
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json("""
            {"matrix":{"numOrigins":1,"numDestinations":1,"distances":[2469]},"regionDefinition":{"type":"world"}}
            """));
        var (client, _) = Create(handler);

        var km = await client.GetDrivingDistanceKmAsync(new Coordinate(32.85, 39.92), new Coordinate(32.86, 39.93), default);

        Assert.Equal(2.469, km, 6);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("async=false", request.Uri.Query);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("world", root.GetProperty("regionDefinition").GetProperty("type").GetString());
        Assert.Equal("car", root.GetProperty("transportMode").GetString());
        Assert.Equal(39.92, root.GetProperty("origins")[0].GetProperty("lat").GetDouble());
        Assert.Equal(32.85, root.GetProperty("origins")[0].GetProperty("lng").GetDouble());
        Assert.Equal(39.93, root.GetProperty("destinations")[0].GetProperty("lat").GetDouble());
        Assert.Equal(32.86, root.GetProperty("destinations")[0].GetProperty("lng").GetDouble());
    }

    [Fact]
    public async Task Driving_distance_reads_the_first_entry_of_the_recorded_matrix()
    {
        var (client, _) = Create(Ok("matrix-car.json"));

        var km = await client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default);

        Assert.Equal(2.469, km, 6);
    }

    [Theory]
    [InlineData("""{"matrix":{"numOrigins":1,"numDestinations":1,"errorCodes":[3],"distances":[0]}}""")]
    [InlineData("""{"matrix":{"numOrigins":1,"numDestinations":1}}""")]
    [InlineData("""{"matrix":{"distances":[]}}""")]
    [InlineData("""{"matrix":{"distances":[null]}}""")]
    [InlineData("""{}""")]
    public async Task A_matrix_error_code_or_missing_distance_throws_and_never_returns_zero(string body)
    {
        var (client, _) = Create(new FakeHereHandler((_, _) => FakeHereHandler.Json(body)));

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default));
    }

    [Fact]
    public async Task Matrix_5xx_is_retried_then_throws()
    {
        var handler = new FakeHereHandler((_, _) => FakeHereHandler.Json("{}", HttpStatusCode.BadGateway));
        var (client, _) = Create(handler);

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default));

        Assert.Equal(4, handler.Requests.Count);
    }

    // Configuration
    [Fact]
    public async Task A_missing_key_fails_at_call_time_with_a_clear_message_and_makes_no_request()
    {
        var handler = Ok("isoline-car-time.json");
        var (client, _) = Create(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.PassengerCar, RangeType.Time, [300], default));

        Assert.Contains("Here:ApiKey", ex.Message);
        Assert.Contains("HERE_KEY", ex.Message);
        Assert.DoesNotContain("http", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_missing_key_fails_the_distance_call_the_same_way()
    {
        var handler = Ok("matrix-car.json");
        var (client, _) = Create(handler, apiKey: null);

        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("from-section", "from-env", "from-section")]
    [InlineData(null, "from-env", "from-env")]
    [InlineData(null, null, null)]
    public void ApiKey_binds_from_Here_ApiKey_then_falls_back_to_HERE_KEY(string? section, string? env, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Here:ApiKey"] = section, ["HERE_KEY"] = env }).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config).AddLogging().AddHereClient();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<IOptions<HereOptions>>().Value.ApiKey);
    }

    // (e) + decision 6
    [Fact]
    public async Task The_key_never_appears_in_logs_or_exceptions_when_running_through_DI_on_success_and_failure()
    {
        var logs = new CapturingLoggerProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Here:ApiKey"] = FakeKey }).Build();

        var success = true;
        var handler = new FakeHereHandler((_, _) => success
            ? FakeHereHandler.Json(Fixtures.Read("isoline-car-time.json"))
            : FakeHereHandler.Json("{}", HttpStatusCode.InternalServerError));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IHereDelay>(new RecordingDelay());
        services.AddHereClient();
        services.AddHttpClient<IHereRoutingClient, HereRoutingClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHereRoutingClient>();

        await client.GetIsolinesAsync(Ankara, VehicleType.HeavyDuty, RangeType.Time, [300, 600], default);
        success = false;
        var ex = await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetIsolinesAsync(Ankara, VehicleType.HeavyDuty, RangeType.Distance, [2000], default));
        await Assert.ThrowsAsync<HereUnavailableException>(() =>
            client.GetDrivingDistanceKmAsync(Ankara, new Coordinate(32.86, 39.93), default));

        // The key really was sent, so the absence below is meaningful.
        Assert.All(handler.Requests, r => Assert.Contains(FakeKey, r.Uri.Query));
        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, l => l.Contains("HeavyDuty") && l.Contains("Time") && l.Contains("300,600"));
        Assert.All(logs.Lines, l =>
        {
            Assert.DoesNotContain(FakeKey, l);
            Assert.DoesNotContain("apiKey=", l);
            Assert.DoesNotContain("isoline.router.hereapi.com", l);
        });
        Assert.DoesNotContain(FakeKey, ex.ToString());
    }

    private static string EncodeRing((int Lat, int Lng)[] points)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var sb = new System.Text.StringBuilder();
        void Write(long value)
        {
            var v = value < 0 ? ~(value << 1) : value << 1;
            do
            {
                var chunk = (int)(v & 0x1F);
                v >>= 5;
                if (v > 0) chunk |= 0x20;
                sb.Append(alphabet[chunk]);
            } while (v > 0);
        }
        void WriteUnsigned(long v)
        {
            do
            {
                var chunk = (int)(v & 0x1F);
                v >>= 5;
                if (v > 0) chunk |= 0x20;
                sb.Append(alphabet[chunk]);
            } while (v > 0);
        }
        WriteUnsigned(1);
        WriteUnsigned(5); // precision 5, no third dimension
        long lat = 0, lng = 0;
        foreach (var p in points)
        {
            Write(p.Lat * 100000L - lat);
            Write(p.Lng * 100000L - lng);
            lat = p.Lat * 100000L;
            lng = p.Lng * 100000L;
        }
        return sb.ToString();
    }
}
