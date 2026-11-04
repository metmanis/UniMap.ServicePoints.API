using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.Tests.Distances;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class DistanceEndpointsTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        await _seed.Point(1, "A", 33.0, 39.0);
        await _seed.Point(2, "B", 33.1, 39.1);
        await _seed.Point(3, "Inactive", active: false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(IHereRoutingClient here) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
        b.ConfigureTestServices(s => s.AddSingleton(here));
    }).CreateClient();

    [Fact]
    public async Task Returns_the_driving_distance_between_two_points()
    {
        var here = new FakeDistanceHere(12.5);

        var response = await Client(here).GetAsync("/api/service-points/distance?from=1&to=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("from").GetInt32());
        Assert.Equal(2, body.GetProperty("to").GetInt32());
        Assert.Equal(12.5, body.GetProperty("distanceKm").GetDouble());
    }

    [Fact]
    public async Task The_same_point_twice_is_zero_without_calling_HERE()
    {
        var here = new FakeDistanceHere();

        var response = await Client(here).GetAsync("/api/service-points/distance?from=1&to=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("distanceKm").GetDouble());
        Assert.Empty(here.Calls);
    }

    [Theory]
    [InlineData("/api/service-points/distance?from=1&to=3")]
    [InlineData("/api/service-points/distance?from=1&to=999")]
    public async Task An_inactive_or_unknown_point_is_404_without_calling_HERE(string url)
    {
        var here = new FakeDistanceHere();

        var response = await Client(here).GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(here.Calls);
    }

    [Theory]
    [InlineData("/api/service-points/distance")]
    [InlineData("/api/service-points/distance?from=1")]
    [InlineData("/api/service-points/distance?to=2")]
    [InlineData("/api/service-points/distance?from=abc&to=2")]
    [InlineData("/api/service-points/distance?from=1&to=1.5")]
    public async Task A_missing_or_non_integer_id_is_400_with_a_message(string url)
    {
        var here = new FakeDistanceHere();

        var response = await Client(here).GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(here.Calls);
    }

    [Fact]
    public async Task A_HERE_failure_is_503_with_a_message_and_no_distance()
    {
        var here = new FakeDistanceHere { Fail = true };

        var response = await Client(here).GetAsync("/api/service-points/distance?from=1&to=2");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(body.TryGetProperty("distanceKm", out _));
    }

    [Fact]
    public async Task A_negative_distance_from_HERE_is_503_not_a_distance()
    {
        var response = await Client(new FakeDistanceHere(-1)).GetAsync("/api/service-points/distance?from=1&to=2");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
