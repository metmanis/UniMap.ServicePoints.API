using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.Tests.Endpoints;

public class ConfigEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Limits_returns_only_the_four_configured_limits()
    {
        var response = await factory.CreateClient().GetAsync("/api/config/limits");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var names = json.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["maxDistanceKm", "maxDriveTimeMinutes", "minDistanceKm", "minDriveTimeMinutes"], names);

        var limits = json.Deserialize<ServicePointLimits>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(limits.MinDriveTimeMinutes > 0);
        Assert.True(limits.MaxDriveTimeMinutes >= limits.MinDriveTimeMinutes);
        Assert.True(limits.MinDistanceKm > 0);
        Assert.True(limits.MaxDistanceKm >= limits.MinDistanceKm);
    }

    private static HttpClient ClientWith(WebApplicationFactory<Program> factory, params (string Key, string? Value)[] overrides) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>($"ServicePointLimits:{o.Key}", o.Value)))))
            .CreateClient();

    [Fact]
    public void Missing_keys_fail_startup_and_name_the_keys()
    {
        var ex = Assert.Throws<OptionsValidationException>(() =>
            ClientWith(factory, ("MinDriveTimeMinutes", null), ("MaxDistanceKm", null)));

        Assert.Contains(ex.Failures, f => f.Contains("ServicePointLimits:MinDriveTimeMinutes"));
        Assert.Contains(ex.Failures, f => f.Contains("ServicePointLimits:MaxDistanceKm"));
        Assert.DoesNotContain(ex.Failures, f => f.Contains("ServicePointLimits:MaxDriveTimeMinutes"));
    }

    [Fact]
    public void Inverted_limits_fail_startup()
    {
        var ex = Assert.Throws<OptionsValidationException>(() =>
            ClientWith(factory, ("MinDistanceKm", "50"), ("MaxDistanceKm", "10")));

        Assert.Contains(ex.Failures, f => f.Contains("MaxDistanceKm must not be below MinDistanceKm"));
    }

    [Fact]
    public void Non_numeric_value_fails_startup_naming_the_key()
    {
        var ex = Assert.ThrowsAny<Exception>(() => ClientWith(factory, ("MaxDriveTimeMinutes", "abc")));

        Assert.Contains("ServicePointLimits:MaxDriveTimeMinutes", ex.ToString());
    }
}
