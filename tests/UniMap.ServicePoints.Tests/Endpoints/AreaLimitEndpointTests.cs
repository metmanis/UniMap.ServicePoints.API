using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class AreaLimitEndpointTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);

    public Task InitializeAsync() => _seed.Clear();

    public Task DisposeAsync() => Task.CompletedTask;

    // A new host per call, so each test starts with an empty one-hour cache.
    private async Task<JsonElement> GetAreaLimit()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString)).CreateClient();
        var response = await client.GetAsync("/api/config/area-limit");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Returns_the_area_of_the_largest_seeded_city_in_square_kilometres()
    {
        await _seed.City(1, "Small", 33.0, 39.0, 33.1, 39.1);
        await _seed.City(2, "Big", 33.0, 39.0, 33.4, 39.4);
        await using var c = await db.OpenAsync();
        var bigCity = "(SELECT \"Geometry\" FROM \"Administrative\".\"Cities\" WHERE \"Id\" = 2)";
        await using var cmd = new NpgsqlCommand($"SELECT {GapMeasure.SquareKmSql(bigCity)}", c);
        var expected = (double)(await cmd.ExecuteScalarAsync())!;

        var json = await GetAreaLimit();

        Assert.Equal(["maxAreaKm2"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(expected, json.GetProperty("maxAreaKm2").GetDouble(), 6);
        Assert.InRange(expected, 1000, 3000);
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_usable_city()
    {
        var json = await GetAreaLimit();

        Assert.Equal(JsonValueKind.Null, json.GetProperty("maxAreaKm2").ValueKind);
    }
}
