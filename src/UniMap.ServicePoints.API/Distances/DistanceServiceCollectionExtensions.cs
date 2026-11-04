namespace UniMap.ServicePoints.API.Distances;

public static class DistanceServiceCollectionExtensions
{
    public static IServiceCollection AddDistances(this IServiceCollection services)
    {
        services.AddSingleton<DistanceCache>();
        services.AddScoped<DistanceService>();
        return services;
    }
}
