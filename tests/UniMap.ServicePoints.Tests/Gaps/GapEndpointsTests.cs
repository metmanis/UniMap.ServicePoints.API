using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Isochrones;
using UniMap.ServicePoints.Tests.Here;
using UniMap.ServicePoints.Tests.Support;
using static UniMap.ServicePoints.Tests.Gaps.GapShapes;

namespace UniMap.ServicePoints.Tests.Gaps;

public class GapEndpointsTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    // Longitude first, as GeoJSON requires. Latitude 33 to 33.2 would be a different place entirely.
    private const string Area = """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.2,39.0],[33.2,39.2],[33.0,39.2],[33.0,39.0]]]}""";

    private readonly ServicePointSeed _seed = new(db);

    public Task InitializeAsync() => _seed.Clear();

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(IHereRoutingClient here) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
        b.ConfigureTestServices(s => s.AddSingleton(here));
    }).CreateClient();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Body(string area = Area, string vehicle = "\"PassengerCar\"", string minutes = "30", string km = "null") =>
        $$"""{"area":{{area}},"vehicle":{{vehicle}},"driveTimeMinutes":{{minutes}},"distanceKm":{{km}}}""";

    private async Task AddPoint(int id, double x, double y = 39.1)
    {
        await _seed.Point(id, $"P{id}", x, y);
        await _seed.VehicleType(id, "PassengerCar");
    }

    private static ScriptedHere NoHere() => new(NetTopologySuite.Geometries.MultiPolygon (NetTopologySuite.Geometries.Coordinate _, RangeType _, int _) =>
        throw new InvalidOperationException("HERE must not be called in this test."));

    [Fact]
    public async Task Success_returns_the_gaps_as_a_geojson_multipolygon_with_longitude_first()
    {
        await AddPoint(1, 33.05, 39.1);
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gaps = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gaps");
        Assert.Equal("MultiPolygon", gaps.GetProperty("type").GetString());
        var ring = gaps.GetProperty("coordinates")[0][0].EnumerateArray().ToArray();
        Assert.All(ring, p =>
        {
            Assert.InRange(p[0].GetDouble(), 33.1, 33.2); // longitude
            Assert.InRange(p[1].GetDouble(), 39.0, 39.2); // latitude
        });
        var origin = Assert.Single(here.Requests).Origin;
        Assert.Equal(33.05, origin.X);
        Assert.Equal(39.1, origin.Y);
    }

    [Fact]
    public async Task No_gaps_returns_200_with_an_empty_multipolygon()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(32, 38, 34, 40));

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gaps = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gaps");
        Assert.Equal("MultiPolygon", gaps.GetProperty("type").GetString());
        Assert.Equal(0, gaps.GetProperty("coordinates").GetArrayLength());
    }

    [Fact]
    public async Task A_second_identical_request_through_the_endpoint_uses_the_cache()
    {
        await AddPoint(1, 33.05);
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));
        var client = Client(here);

        var first = await (await client.PostAsync("/api/gaps", Json(Body()))).Content.ReadAsStringAsync();
        var second = await (await client.PostAsync("/api/gaps", Json(Body()))).Content.ReadAsStringAsync();

        Assert.Equal(first, second);
        Assert.Single(here.Requests);
    }

    private const string TooBigArea = """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.5,39.0],[33.5,39.5],[33.0,39.5],[33.0,39.0]]]}""";

    [Theory]
    [InlineData("/api/gaps")]
    [InlineData("/api/gaps/export?format=csv")]
    [InlineData("/api/gaps/export?format=xlsx")]
    public async Task An_area_larger_than_the_largest_city_returns_400_naming_both_areas_and_calls_no_HERE(string url)
    {
        await _seed.City(1, "Big", 33.0, 39.0, 33.4, 39.4);
        await AddPoint(1, 33.05);
        var here = NoHere();

        var response = await Client(here).PostAsync(url, Json(Body(area: TooBigArea)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        var message = Assert.Single(errors.EnumerateArray()).GetString()!;
        Assert.Contains("km", message);
        Assert.Matches(@"\d.*km.*\d.*km", message);
        Assert.Empty(here.Requests);
    }

    [Fact]
    public async Task An_area_inside_the_largest_city_is_calculated_as_before()
    {
        await _seed.City(1, "Big", 33.0, 39.0, 33.4, 39.4);
        await AddPoint(1, 33.05);
        var here = new ScriptedHere((_, _, _) => Rect(32.9, 38.9, 33.1, 39.3));

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(here.Requests);
    }

    public static TheoryData<string, string> BadBodies() => new()
    {
        { "malformed json", "{not json" },
        { "empty body", "" },
        { "array root", "[]" },
        { "missing area", """{"vehicle":"PassengerCar","driveTimeMinutes":30}""" },
        { "null area", Body(area: "null") },
        { "area not an object", Body(area: "5") },
        { "wrong geometry type", Body(area: """{"type":"Point","coordinates":[33.0,39.0]}""") },
        { "multipolygon", Body(area: """{"type":"MultiPolygon","coordinates":[[[[33.0,39.0],[33.2,39.0],[33.2,39.2],[33.0,39.0]]]]}""") },
        { "feature", Body(area: """{"type":"Feature","geometry":null,"properties":{}}""") },
        { "no coordinates", Body(area: """{"type":"Polygon"}""") },
        { "coordinates not arrays", Body(area: """{"type":"Polygon","coordinates":5}""") },
        { "position of one number", Body(area: """{"type":"Polygon","coordinates":[[[33.0],[33.2],[33.2],[33.0]]]}""") },
        { "position of strings", Body(area: """{"type":"Polygon","coordinates":[[["a","b"],["c","d"],["e","f"],["a","b"]]]}""") },
        { "ring not closed", Body(area: """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.2,39.0],[33.2,39.2],[33.0,39.2]]]}""") },
        { "ring too short", Body(area: """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.0,39.0]]]}""") },
        { "empty polygon", Body(area: """{"type":"Polygon","coordinates":[]}""") },
        { "zero area", Body(area: """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.1,39.1],[33.2,39.2],[33.0,39.0]]]}""") },
        { "latitude and longitude swapped out of range", Body(area: """{"type":"Polygon","coordinates":[[[33.0,95.0],[33.2,95.0],[33.2,95.2],[33.0,95.0]]]}""") },
        { "coordinate too large for a number", Body(area: """{"type":"Polygon","coordinates":[[[33.0,1e999],[33.2,39.0],[33.2,39.2],[33.0,1e999]]]}""") },
        { "missing vehicle", """{"area":{"type":"Polygon","coordinates":[[[33.0,39.0],[33.2,39.0],[33.2,39.2],[33.0,39.0]]]},"driveTimeMinutes":30}""" },
        { "unknown vehicle", Body(vehicle: "\"Motorcycle\"") },
        { "numeric enum text", Body(vehicle: "\"1\"") },
        { "numeric vehicle", Body(vehicle: "1") },
        { "text drive time", Body(minutes: "\"30\"") },
        { "fractional distance", Body(km: "1.5") },
        { "no thresholds", Body(minutes: "null", km: "null") },
        { "threshold over the limit", Body(minutes: "61") },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_request_returns_400_with_error_messages(string _, string body)
    {
        var response = await Client(NoHere()).PostAsync("/api/gaps", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.GetArrayLength() > 0);
        Assert.All(errors.EnumerateArray(), e => Assert.False(string.IsNullOrWhiteSpace(e.GetString())));
    }

    [Fact]
    public async Task A_bow_tie_returns_200()
    {
        await AddPoint(1, 40);

        var response = await Client(NoHere()).PostAsync("/api/gaps", Json(Body(
            area: """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.2,39.2],[33.2,39.0],[33.0,39.2],[33.0,39.0]]]}""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Insufficient_data_returns_422_with_the_message()
    {
        var response = await Client(NoHere()).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Not enough spatial data", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_here_failure_returns_503_with_a_clear_message_and_no_gaps()
    {
        await AddPoint(1, 33.05);
        await AddPoint(2, 33.15);
        var here = new ScriptedHere((o, _, _) => o.X == 33.15
            ? throw new HereUnavailableException("rejected")
            : Rect(32, 38, 34, 40));

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("HERE", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("gaps", out var _));
    }

    [Fact]
    public async Task A_here_answer_with_no_area_returns_422_naming_the_service_point()
    {
        await AddPoint(17, 33.05);
        var here = new ScriptedHere((_, _, _) => Gf.CreateMultiPolygon());

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HERE found no reachable area from the location of service point 17. Check that point's location.", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("gaps", out var _));
    }

    [Fact]
    public async Task An_empty_isoline_from_here_returns_422_naming_the_service_point()
    {
        await AddPoint(17, 33.05);
        var here = new ScriptedHere(NetTopologySuite.Geometries.MultiPolygon (NetTopologySuite.Geometries.Coordinate _, RangeType _, int _) =>
            throw new HereEmptyIsolineException("empty"));

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("service point 17", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Calculation_attempts_running_out_return_503_saying_points_kept_changing()
    {
        await AddPoint(1, 33.05);
        var nextId = 2;
        var here = new ScriptedHere(async (o, _, _) =>
        {
            await AddPoint(Interlocked.Increment(ref nextId), 33.15);
            return Rect(32, 38, 34, 40);
        });

        var response = await Client(here).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("kept changing", message);
        Assert.DoesNotContain("HERE", message);
    }

    [Fact]
    public async Task The_response_holds_the_excluded_count_also_with_no_gaps()
    {
        await AddPoint(1, 33.1);
        await _seed.Point(2, "No geometry", hasGeometry: false);
        await _seed.VehicleType(2, "PassengerCar");
        var here = new ScriptedHere((_, _, _) => Rect(32, 38, 34, 40));

        var body = await (await Client(here).PostAsync("/api/gaps", Json(Body()))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("gaps").GetProperty("coordinates").GetArrayLength());
        Assert.Equal(1, body.GetProperty("excludedPointCount").GetInt32());
    }

    private static string RingOf(int positions)
    {
        var points = Enumerable.Range(0, positions - 1).Select(i =>
        {
            var angle = 2 * Math.PI * i / (positions - 1);
            return FormattableString.Invariant($"[{33.1 + 0.1 * Math.Cos(angle)},{39.1 + 0.1 * Math.Sin(angle)}]");
        }).ToList();
        points.Add(points[0]);
        return $$"""{"type":"Polygon","coordinates":[[{{string.Join(',', points)}}]]}""";
    }

    [Fact]
    public async Task An_area_over_the_vertex_cap_returns_400_with_errors()
    {
        var response = await Client(NoHere()).PostAsync("/api/gaps", Json(Body(area: RingOf(GapRequestParser.MaxAreaVertices + 1))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains("5,000", errors[0].GetString());
    }

    [Fact]
    public async Task An_area_at_the_vertex_cap_is_accepted_by_the_parser()
    {
        Assert.Equal(5000, GapRequestParser.MaxAreaVertices);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Body(area: RingOf(GapRequestParser.MaxAreaVertices))));

        var parsed = await GapRequestParser.ParseAsync(body, default);

        Assert.Empty(parsed.Errors);
        Assert.Equal(GapRequestParser.MaxAreaVertices, parsed.Request!.Area.NumPoints);
    }

    [Fact]
    public async Task A_stored_row_with_no_area_returns_422_naming_the_service_point()
    {
        await AddPoint(1, 33.05);
        await _seed.Exec("""
            INSERT INTO "Services"."ServicePointIsochrones"
                ("ServicePointId", "VehicleType", "RangeType", "RangeValue", "Geometry", "ComputedAt")
            VALUES (1, 'car', 'time', 1800, ST_GeomFromText('MULTIPOLYGON EMPTY', 4326), now())
            """);

        var response = await Client(NoHere()).PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("service point 1.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_missing_here_key_returns_503_and_makes_no_request()
    {
        await AddPoint(1, 33.05);
        var handler = new FakeHereHandler((_, _) => throw new InvalidOperationException("No request may be sent."));
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
            b.ConfigureTestServices(s =>
            {
                s.PostConfigure<HereOptions>(o => o.ApiKey = null);
                s.AddHttpClient<IHereRoutingClient, HereRoutingClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        }).CreateClient();

        var response = await client.PostAsync("/api/gaps", Json(Body()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_app_binds_the_top_speed_option()
    {
        var host = factory.WithWebHostBuilder(b => b.UseSetting("Gaps:MaxSpeedKmh", "99"));

        Assert.Equal(99, host.Services.GetRequiredService<IOptions<GapOptions>>().Value.MaxSpeedKmh);
    }
}
