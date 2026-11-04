using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UniMap.ServicePoints.API.ServicePoints;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class ServicePointEndpointsTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
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

    [Fact]
    public async Task Repeated_query_parameters_filter_with_or_inside_a_field()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.City(2, "Izmir", 20, 0, 30, 10);
        await _seed.Point(1, "A", x: 2, y: 2);
        await _seed.Point(2, "B", x: 22, y: 2);
        await _seed.Point(3, "C", x: 50, y: 50);
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));
        await _seed.LinkBrand(2, await _seed.Brand("Işık"));

        var byCity = await GetJson("/api/service-points?cityIds=1&cityIds=2");
        var byBrand = await GetJson("/api/service-points?brands=bosch&brands=ISIK");
        var all = await GetJson("/api/service-points");

        Assert.Equal([1, 2], byCity.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).Order());
        Assert.Equal([1, 2], byBrand.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).Order());
        Assert.Equal(3, all.GetArrayLength());
    }

    [Fact]
    public async Task Response_uses_camel_case_summary_fields()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "Shell", x: 2, y: 3, address: "Main St 1", code: 42);

        var point = (await GetJson("/api/service-points"))[0];

        Assert.Equal("42", point.GetProperty("code").GetString());
        Assert.Equal("Main St 1", point.GetProperty("address").GetString());
        Assert.Equal("Ankara", point.GetProperty("city").GetString());
        Assert.Equal(3, point.GetProperty("latitude").GetDouble());
        Assert.Equal(2, point.GetProperty("longitude").GetDouble());
        Assert.Equal(JsonValueKind.Array, point.GetProperty("brands").ValueKind);
    }

    [Theory]
    [InlineData("/api/service-points?vehicleTypes=Bogus", "Bogus")]
    [InlineData("/api/service-points?vehicleTypes=PassengerCar&vehicleTypes=1", "1")]
    [InlineData("/api/service-points?cityIds=abc", "abc")]
    [InlineData("/api/service-points?districtIds=1&districtIds=x", "x")]
    public async Task Invalid_values_return_400_naming_the_value(string url, string value)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(value, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Suggestions_return_matches_and_cap_at_ten()
    {
        for (var i = 1; i <= 12; i++) await _seed.Point(i, $"Shell {i:00}");
        await _seed.Point(50, "Kaya");

        var json = await GetJson("/api/service-points/suggestions?q=sh");

        Assert.Equal(10, json.GetArrayLength());
    }

    [Fact]
    public async Task Suggestions_include_a_point_without_a_location_and_the_list_does_not()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "Shell Map", x: 2, y: 3);
        await _seed.Point(2, "Shell Nowhere", hasGeometry: false, cityText: "Konya", districtText: "Selçuklu");
        await _seed.Point(3, "Shell Offshore", x: 200, cityText: "Izmir");
        await _seed.Point(4, "Shell Closed", active: false, hasGeometry: false);

        var suggestions = (await GetJson("/api/service-points/suggestions?q=shell")).EnumerateArray().ToArray();
        var list = await GetJson("/api/service-points");

        Assert.Equal([1, 2, 3], suggestions.Select(e => e.GetProperty("id").GetInt32()).Order());
        var map = suggestions.Single(e => e.GetProperty("id").GetInt32() == 1);
        Assert.True(map.GetProperty("hasLocation").GetBoolean());
        Assert.Equal(3, map.GetProperty("latitude").GetDouble());
        Assert.Equal("Ankara", map.GetProperty("city").GetString());
        var nowhere = suggestions.Single(e => e.GetProperty("id").GetInt32() == 2);
        Assert.False(nowhere.GetProperty("hasLocation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nowhere.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, nowhere.GetProperty("longitude").ValueKind);
        Assert.Equal("Konya", nowhere.GetProperty("city").GetString());
        Assert.Equal("Selçuklu", nowhere.GetProperty("district").GetString());
        var offshore = suggestions.Single(e => e.GetProperty("id").GetInt32() == 3);
        Assert.False(offshore.GetProperty("hasLocation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offshore.GetProperty("longitude").ValueKind);
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(1, list[0].GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("/api/service-points/suggestions")]
    [InlineData("/api/service-points/suggestions?q=s")]
    public async Task Suggestions_with_missing_or_short_query_are_empty(string url)
    {
        await _seed.Point(1, "Shell");

        Assert.Equal(0, (await GetJson(url)).GetArrayLength());
    }

    [Fact]
    public async Task Suggestions_accept_a_query_at_the_maximum_length()
    {
        var name = new string('a', ServicePointQuery.MaxSearchLength);
        await _seed.Point(1, name);

        Assert.Equal(100, ServicePointQuery.MaxSearchLength);
        Assert.Equal(1, (await GetJson($"/api/service-points/suggestions?q={name}")).GetArrayLength());
    }

    [Fact]
    public async Task Surrounding_spaces_do_not_count_towards_the_maximum_length()
    {
        var name = new string('a', ServicePointQuery.MaxSearchLength);
        await _seed.Point(1, name);

        Assert.Equal(1, (await GetJson($"/api/service-points/suggestions?q=%20%20{name}%20%20")).GetArrayLength());
    }

    [Fact]
    public async Task Suggestions_refuse_a_longer_query_with_400_and_a_clear_message()
    {
        var response = await _client.GetAsync($"/api/service-points/suggestions?q={new string('a', ServicePointQuery.MaxSearchLength + 1)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("100", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Filter_options_endpoint_returns_all_five_lists()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10, cityId: 1);
        await _seed.Brand("Bosch");
        await _seed.Point(1, "A", type: "Authorized");

        var json = await GetJson("/api/service-points/filter-options");

        Assert.Equal(["brands", "cities", "districts", "types", "vehicleTypes"],
            json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Ankara", json.GetProperty("cities")[0].GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("cities")[0].GetProperty("id").GetInt32());
        Assert.Equal("Çankaya", json.GetProperty("districts")[0].GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("districts")[0].GetProperty("cityId").GetInt32());
        Assert.Equal("Bosch", json.GetProperty("brands")[0].GetString());
        Assert.Equal("Authorized", json.GetProperty("types")[0].GetString());
        Assert.Equal(["HeavyDuty", "PassengerCar"], json.GetProperty("vehicleTypes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Detail_returns_every_field_for_a_located_point_with_view_derived_city_and_district()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10);
        await _seed.Point(7, "Shell", x: 2, y: 3, address: "Main St 1", code: 42, type: "Authorized",
            cityText: "Wrong", districtText: "Wrong");
        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "Phone" = '555 1234', "IsStale" = true WHERE "Id" = 7""");
        await _seed.LinkBrand(7, await _seed.Brand("Bosch"));
        await _seed.LinkBrand(7, await _seed.Brand("Aral"));
        await _seed.VehicleType(7, "PassengerCar");
        await _seed.VehicleType(7, "HeavyDuty");

        var json = await GetJson("/api/service-points/7");

        Assert.Equal(7, json.GetProperty("id").GetInt32());
        Assert.Equal(42, json.GetProperty("code").GetInt64());
        Assert.Equal("Shell", json.GetProperty("name").GetString());
        Assert.Equal("Authorized", json.GetProperty("type").GetString());
        Assert.Equal("Main St 1", json.GetProperty("address").GetString());
        Assert.Equal("Ankara", json.GetProperty("city").GetString());
        Assert.Equal("Çankaya", json.GetProperty("district").GetString());
        Assert.Equal("555 1234", json.GetProperty("phone").GetString());
        Assert.Equal(["Aral", "Bosch"], json.GetProperty("brands").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, json.GetProperty("latitude").GetDouble());
        Assert.Equal(2, json.GetProperty("longitude").GetDouble());
        Assert.Equal(["HeavyDuty", "PassengerCar"], json.GetProperty("vehicleTypes").EnumerateArray().Select(e => e.GetString()));
        Assert.True(json.GetProperty("isStale").GetBoolean());
        Assert.True(json.GetProperty("hasLocation").GetBoolean());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 200)]
    public async Task Detail_of_an_active_point_without_a_valid_location_has_null_coordinates_and_text_city(bool hasGeometry, double x)
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(8, "Nowhere", x: x, hasGeometry: hasGeometry, cityText: "Konya", districtText: "Selçuklu");

        var json = await GetJson("/api/service-points/8");

        Assert.False(json.GetProperty("hasLocation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("longitude").ValueKind);
        Assert.Equal("Konya", json.GetProperty("city").GetString());
        Assert.Equal("Selçuklu", json.GetProperty("district").GetString());
    }

    [Theory]
    [InlineData("/api/service-points/999")]
    [InlineData("/api/service-points/9")]
    [InlineData("/api/service-points/10")]
    public async Task Detail_is_404_for_unknown_inactive_and_null_active_points(string url)
    {
        await _seed.Point(9, "Closed", active: false);
        await _seed.Point(10, "Unknown", active: null);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_numeric_route_keeps_suggestions_filter_options_and_distance_reachable()
    {
        await _seed.Point(1, "Shell");

        var suggestions = await _client.GetAsync("/api/service-points/suggestions?q=shell");
        var options = await _client.GetAsync("/api/service-points/filter-options");
        var distance = await _client.GetAsync("/api/service-points/distance");

        Assert.Equal(HttpStatusCode.OK, suggestions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, distance.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, distance.StatusCode);
    }
}
