using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using UniMap.ServicePoints.API.Boundaries;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Boundaries;

public class BoundaryQueryTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _seed.Clear();
        _dataSource = NpgsqlDataSource.Create(db.ConnectionString);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private BoundaryQuery Query(string? tolerance = null) => new(_dataSource, Options.Create(
        tolerance is null ? new BoundaryOptions() : new BoundaryOptions { SimplifyToleranceDegrees = double.Parse(tolerance, CultureInfo.InvariantCulture) }));

    private HttpClient Client(string? tolerance = null) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
        if (tolerance is not null) b.UseSetting("Boundaries:SimplifyToleranceDegrees", tolerance);
    }).CreateClient();

    // Circle of radius 1 degree with 257 vertices; deviation from a straight chord is far below 0.0005 degrees.
    private Task DetailedCity(int id) => _seed.Exec($"""
        INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry")
        VALUES ({id}, 'Round', ST_Multi(ST_Buffer(ST_SetSRID(ST_MakePoint(100, 10), 4326), 1, 64)))
        """);

    private static int VertexCount(JsonElement feature)
    {
        var count = 0;
        void Walk(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array) return;
            if (e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.Number) count++;
            else foreach (var child in e.EnumerateArray()) Walk(child);
        }
        Walk(feature.GetProperty("geometry").GetProperty("coordinates"));
        return count;
    }

    private static void AssertSingleFeature(JsonElement feature, int id, string name)
    {
        Assert.Equal("Feature", feature.GetProperty("type").GetString());
        Assert.Equal(id, feature.GetProperty("properties").GetProperty("id").GetInt32());
        Assert.Equal(name, feature.GetProperty("properties").GetProperty("name").GetString());
        var geometry = feature.GetProperty("geometry");
        Assert.Contains(geometry.GetProperty("type").GetString(), new[] { "Polygon", "MultiPolygon" });
        Assert.False(feature.TryGetProperty("features", out _));
    }

    [Fact]
    public async Task District_returns_a_single_feature_with_lng_lat_coordinates()
    {
        await _seed.District(7, "Cankaya", 100, 10, 110, 20);

        var json = await Query().GetBoundaryGeoJsonAsync(BoundaryKind.District, 7, default);

        var feature = JsonDocument.Parse(json!).RootElement;
        AssertSingleFeature(feature, 7, "Cankaya");
        var geometry = feature.GetProperty("geometry");
        var ring = geometry.GetProperty("type").GetString() == "Polygon"
            ? geometry.GetProperty("coordinates")[0]
            : geometry.GetProperty("coordinates")[0][0];
        var first = ring[0];
        Assert.InRange(first[0].GetDouble(), 100, 110); // longitude first
        Assert.InRange(first[1].GetDouble(), 10, 20);   // latitude second
    }

    [Fact]
    public async Task City_returns_the_city_not_a_district_with_the_same_id()
    {
        await _seed.City(3, "Ankara", 100, 10, 110, 20);
        await _seed.District(3, "Other", 0, 0, 1, 1);

        var json = await Query().GetBoundaryGeoJsonAsync(BoundaryKind.City, 3, default);

        AssertSingleFeature(JsonDocument.Parse(json!).RootElement, 3, "Ankara");
    }

    [Fact]
    public async Task Unknown_id_returns_null()
    {
        await _seed.City(1, "Ankara", 0, 0, 1, 1);

        Assert.Null(await Query().GetBoundaryGeoJsonAsync(BoundaryKind.City, 99, default));
        Assert.Null(await Query().GetBoundaryGeoJsonAsync(BoundaryKind.District, 1, default));
    }

    [Fact]
    public async Task Null_geometry_behaves_like_an_unknown_id()
    {
        await _seed.Exec("""INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry") VALUES (5, 'Empty', NULL)""");

        Assert.Null(await Query().GetBoundaryGeoJsonAsync(BoundaryKind.City, 5, default));
    }

    [Fact]
    public async Task Null_name_is_returned_as_json_null()
    {
        await _seed.Exec("""
            INSERT INTO "Administrative"."Districts" ("Id", "Name", "Geometry")
            VALUES (6, NULL, ST_Multi(ST_MakeEnvelope(0, 0, 1, 1, 4326)))
            """);

        var json = await Query().GetBoundaryGeoJsonAsync(BoundaryKind.District, 6, default);

        var name = JsonDocument.Parse(json!).RootElement.GetProperty("properties").GetProperty("name");
        Assert.Equal(JsonValueKind.Null, name.ValueKind);
    }

    [Fact]
    public async Task Detailed_polygon_is_simplified_with_the_default_tolerance()
    {
        await DetailedCity(1);

        var simplified = JsonDocument.Parse((await Query().GetBoundaryGeoJsonAsync(BoundaryKind.City, 1, default))!).RootElement;
        var detailed = JsonDocument.Parse((await Query("0").GetBoundaryGeoJsonAsync(BoundaryKind.City, 1, default))!).RootElement;

        Assert.Equal(257, VertexCount(detailed));
        Assert.True(VertexCount(simplified) < VertexCount(detailed), $"simplified has {VertexCount(simplified)} vertices");
    }

    [Fact]
    public async Task Configured_tolerance_is_used()
    {
        await DetailedCity(1);

        var coarse = VertexCount(JsonDocument.Parse((await Query("0.5").GetBoundaryGeoJsonAsync(BoundaryKind.City, 1, default))!).RootElement);
        var fine = VertexCount(JsonDocument.Parse((await Query("0.00001").GetBoundaryGeoJsonAsync(BoundaryKind.City, 1, default))!).RootElement);

        Assert.True(coarse < fine, $"coarse={coarse}, fine={fine}");
    }

    [Fact]
    public async Task Endpoint_applies_the_configured_tolerance()
    {
        await DetailedCity(1);
        using var coarse = Client("0.5");
        using var fine = Client("0");

        var coarseJson = await coarse.GetFromJsonAsync<JsonElement>("/api/boundaries/city/1");
        var fineJson = await fine.GetFromJsonAsync<JsonElement>("/api/boundaries/city/1");

        Assert.Equal(257, VertexCount(fineJson));
        Assert.True(VertexCount(coarseJson) < 257);
    }

    [Theory]
    [InlineData("district")]
    [InlineData("District")]
    [InlineData("DISTRICT")]
    public async Task Endpoint_returns_geo_json_for_district_case_insensitively(string kind)
    {
        await _seed.District(7, "Cankaya", 100, 10, 110, 20);
        using var client = Client();

        var response = await client.GetAsync($"/api/boundaries/{kind}/7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/geo+json", response.Content.Headers.ContentType?.MediaType);
        AssertSingleFeature(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement, 7, "Cankaya");
    }

    [Fact]
    public async Task Endpoint_returns_geo_json_for_city()
    {
        await _seed.City(3, "Ankara", 100, 10, 110, 20);
        using var client = Client();

        var feature = await client.GetFromJsonAsync<JsonElement>("/api/boundaries/city/3");

        AssertSingleFeature(feature, 3, "Ankara");
    }

    [Theory]
    [InlineData("/api/boundaries/city/99")]
    [InlineData("/api/boundaries/district/99")]
    public async Task Endpoint_returns_404_for_unknown_id(string url)
    {
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Endpoint_returns_404_for_a_null_geometry()
    {
        await _seed.Exec("""INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry") VALUES (5, 'Empty', NULL)""");
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/boundaries/city/5")).StatusCode);
    }

    [Theory]
    [InlineData("/api/boundaries/region/1", "region")]
    [InlineData("/api/boundaries/0/1", "0")]
    [InlineData("/api/boundaries/1/1", "1")]
    public async Task Endpoint_returns_400_for_an_unknown_kind(string url, string kind)
    {
        using var client = Client();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(kind, body);
        Assert.Contains("city", body);
    }

    [Fact]
    public async Task Endpoint_returns_404_for_a_non_integer_id_because_the_route_does_not_match()
    {
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/boundaries/city/abc")).StatusCode);
    }

    [Fact]
    public async Task Small_district_about_100_m_across_keeps_a_valid_polygon_at_the_default_tolerance()
    {
        // 0.001 degrees is about 100 m, only twice the default tolerance.
        await _seed.District(8, "Tiny", 100, 10, 100.001, 10.001);

        var json = await Query().GetBoundaryGeoJsonAsync(BoundaryKind.District, 8, default);

        var feature = JsonDocument.Parse(json!).RootElement;
        AssertSingleFeature(feature, 8, "Tiny");
        var geometry = feature.GetProperty("geometry");
        var ring = geometry.GetProperty("type").GetString() == "Polygon"
            ? geometry.GetProperty("coordinates")[0]
            : geometry.GetProperty("coordinates")[0][0];
        Assert.True(ring.GetArrayLength() >= 4, $"ring has {ring.GetArrayLength()} points");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Invalid_tolerance_fails_startup_naming_the_key(string value)
    {
        var ex = Record.Exception(() =>
        {
            using var client = factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
                b.UseSetting("Boundaries:SimplifyToleranceDegrees", value);
            }).CreateClient();
        });

        Assert.NotNull(ex);
        Assert.Contains("Boundaries:SimplifyToleranceDegrees", ex.ToString());
    }

    [Fact]
    public void Zero_tolerance_is_valid_and_means_no_simplification()
    {
        using var client = Client("0");
    }
}
