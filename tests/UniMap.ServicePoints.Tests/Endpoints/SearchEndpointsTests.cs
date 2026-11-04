using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class SearchEndpointsTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        _client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString)).CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Fields(JsonElement item)
        => item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Returns_mixed_kinds_in_order()
    {
        await _seed.City(7, "Bursa", 0, 0, 10, 10);
        await _seed.District(8, "Bursa", 0, 0, 10, 10, cityId: 7);
        await _seed.District(9, "Bursa", 20, 0, 30, 10);
        await _seed.Point(1, "Bursa", x: 2, y: 3, address: "Main St 1");
        await _seed.Point(2, "Bursa", hasGeometry: false, cityText: "Konya", districtText: "Selçuklu");
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));

        var items = (await GetJson("/api/search?q=bursa")).EnumerateArray().ToArray();

        Assert.Equal(["city", "district", "district", "servicePoint", "servicePoint"],
            items.Select(i => i.GetProperty("kind").GetString()));

        Assert.Equal(["id", "kind", "name"], Fields(items[0]));
        Assert.Equal(7, items[0].GetProperty("id").GetInt32());
        Assert.Equal("Bursa", items[0].GetProperty("name").GetString());

        Assert.Equal(["cityName", "id", "kind", "name"], Fields(items[1]));
        Assert.Equal(8, items[1].GetProperty("id").GetInt32());
        Assert.Equal("Bursa", items[1].GetProperty("name").GetString());
        Assert.Equal("Bursa", items[1].GetProperty("cityName").GetString());
        Assert.Equal(9, items[2].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("cityName").ValueKind);

        string[] pointFields = ["address", "brands", "city", "district", "hasLocation", "id", "kind", "latitude", "longitude", "name"];
        var located = items[3];
        Assert.Equal(pointFields, Fields(located));
        Assert.Equal(1, located.GetProperty("id").GetInt32());
        Assert.Equal("Bursa", located.GetProperty("name").GetString());
        Assert.Equal("Main St 1", located.GetProperty("address").GetString());
        Assert.Equal("Bursa", located.GetProperty("city").GetString());
        Assert.Equal("Bursa", located.GetProperty("district").GetString());
        Assert.Equal(["Bosch"], located.GetProperty("brands").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal(3, located.GetProperty("latitude").GetDouble());
        Assert.Equal(2, located.GetProperty("longitude").GetDouble());
        Assert.True(located.GetProperty("hasLocation").GetBoolean());

        var unlocated = items[4];
        Assert.Equal(pointFields, Fields(unlocated));
        Assert.Equal("Konya", unlocated.GetProperty("city").GetString());
        Assert.Equal("Selçuklu", unlocated.GetProperty("district").GetString());
        Assert.Equal(JsonValueKind.Null, unlocated.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, unlocated.GetProperty("longitude").ValueKind);
        Assert.False(unlocated.GetProperty("hasLocation").GetBoolean());
    }

    [Fact]
    public async Task Over_100_characters_is_400()
    {
        var response = await _client.GetAsync($"/api/search?q={new string('a', 101)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exactly_100_characters_is_accepted()
    {
        var name = new string('a', 100);
        await _seed.Point(1, name);

        Assert.Equal(1, (await GetJson($"/api/search?q={name}")).GetArrayLength());
    }

    [Fact]
    public async Task Spaces_around_q_do_not_count_towards_the_limit()
    {
        var response = await _client.GetAsync($"/api/search?q=%20{new string('a', 100)}%20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/search?q=s")]
    public async Task Missing_q_is_an_empty_list(string url)
    {
        await _seed.Point(1, "Shell");

        Assert.Equal(0, (await GetJson(url)).GetArrayLength());
    }
}
