using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniMap.ServicePoints.API.Export;

namespace UniMap.ServicePoints.API.Gaps;

public static class GapServiceCollectionExtensions
{
    public static IServiceCollection AddGaps(this IServiceCollection services)
    {
        services.AddOptions<GapOptions>()
            .BindConfiguration(GapOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GapOptions>, GapOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<GapCache>();
        services.AddSingleton<AreaLimit>();
        services.AddScoped<IGapService, GapService>();
        services.AddScoped<GapExportService>();
        return services;
    }
}
