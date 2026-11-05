using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.Tests.Routes;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class RouteEndpointsTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Url = "/api/service-points/route";

    private readonly ServicePointSeed _seed = new(db);

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        await _seed.Point(1, "A", 33.0, 39.0);
        await _seed.Point(2, "B", 33.1, 39.1);
        await _seed.Point(3, "Inactive", 33.2, 39.2, active: false);
        await _seed.Point(4, "Unlocated", hasGeometry: false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(IHereRoutingClient here) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
        b.ConfigureTestServices(s => s.AddSingleton(here));
    }).CreateClient();

    private static async Task<string> MessageOf(HttpResponseMessage response)
    {
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        return message;
    }

    [Fact]
    public async Task Returns_the_route()
    {
        var response = await Client(new FakeRouteHere { Meters = 1500.5, Seconds = 120 }).GetAsync($"{Url}?from=1&to=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("from").GetInt32());
        Assert.Equal(2, body.GetProperty("to").GetInt32());
        Assert.Equal(1500.5, body.GetProperty("distanceMeters").GetDouble());
        Assert.Equal(120, body.GetProperty("durationSeconds").GetInt32());
        Assert.False(body.GetProperty("fromCache").GetBoolean());
        Assert.True(body.GetProperty("calculatedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        var geometry = body.GetProperty("geometry");
        Assert.Equal("LineString", geometry.GetProperty("type").GetString());
        var coordinates = geometry.GetProperty("coordinates").EnumerateArray()
            .Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToArray();
        Assert.Equal([(33.0, 39.0), (33.05, 39.01), (33.1, 39.1)], coordinates);
    }

    [Fact]
    public async Task A_second_request_reports_fromCache_true()
    {
        var here = new FakeRouteHere();
        var client = Client(here);

        var first = await (await client.GetAsync($"{Url}?from=1&to=2")).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await client.GetAsync($"{Url}?from=1&to=2")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(first.GetProperty("fromCache").GetBoolean());
        Assert.True(second.GetProperty("fromCache").GetBoolean());
        Assert.Equal(1, here.Calls);
        Assert.Equal(first.GetProperty("distanceMeters").GetDouble(), second.GetProperty("distanceMeters").GetDouble());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?to=2")]
    [InlineData("?from=1")]
    [InlineData("?from=abc&to=2")]
    [InlineData("?from=1&to=1.5")]
    [InlineData("?from=1&to=1")]
    public async Task Bad_parameters_are_400(string query)
    {
        var here = new FakeRouteHere();

        var response = await Client(here).GetAsync(Url + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await MessageOf(response);
        Assert.Equal(0, here.Calls);
    }

    [Theory]
    [InlineData("?from=1&to=999")]
    [InlineData("?from=999&to=2")]
    [InlineData("?from=1&to=3")]
    [InlineData("?from=4&to=2")]
    public async Task Unknown_inactive_or_unlocated_point_is_404(string query)
    {
        var here = new FakeRouteHere();

        var response = await Client(here).GetAsync(Url + query);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await MessageOf(response);
        Assert.Equal(0, here.Calls);
    }

    [Fact]
    public async Task No_route_is_422_with_a_message()
    {
        var here = new FakeRouteHere { Fail = new HereNoRouteException("routes list was empty") };

        var response = await Client(here).GetAsync($"{Url}?from=1&to=2");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var message = await MessageOf(response);
        Assert.Equal("HERE found no driving route between these service points.", message);
        Assert.DoesNotContain("routes list", message);
    }

    [Fact]
    public async Task HERE_unavailable_is_503_without_details()
    {
        const string secret = "https://router.hereapi.com/v8/routes?apiKey=SECRET-KEY";
        var here = new FakeRouteHere { Fail = new HereUnavailableException(secret) };

        var response = await Client(here).GetAsync($"{Url}?from=1&to=2");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-KEY", text);
        Assert.DoesNotContain("hereapi", text);
        Assert.Equal("The HERE routing service is unavailable, so the route could not be calculated.",
            JsonDocument.Parse(text).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_literal_path_is_not_read_as_an_id()
    {
        var response = await Client(new FakeRouteHere()).GetAsync(Url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
