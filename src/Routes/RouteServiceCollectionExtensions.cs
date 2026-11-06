using Microsoft.Extensions.DependencyInjection.Extensions;

namespace UniMap.ServicePoints.API.Routes;

public static class RouteServiceCollectionExtensions
{
    public static IServiceCollection AddRoutes(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<RouteService>();
        return services;
    }
}
