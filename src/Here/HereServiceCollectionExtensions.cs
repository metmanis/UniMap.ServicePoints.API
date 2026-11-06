using Microsoft.Extensions.DependencyInjection.Extensions;

namespace UniMap.ServicePoints.API.Here;

public static class HereServiceCollectionExtensions
{
    public static IServiceCollection AddHereClient(this IServiceCollection services)
    {
        // Startup must not fail without a key; the client checks at call time.
        services.AddOptions<HereOptions>()
            .BindConfiguration(HereOptions.SectionName)
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                    options.ApiKey = configuration[HereOptions.EnvironmentKeyName];
            });

        services.TryAddSingleton<IHereDelay, TaskHereDelay>();

        services.AddHttpClient<IHereRoutingClient, HereRoutingClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            // The key is in the query string and the default HttpClient logging records full URIs.
            .RemoveAllLoggers();

        return services;
    }
}
