using Microsoft.Extensions.Options;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Gaps;

namespace UniMap.ServicePoints.API.Endpoints;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config/limits", (IOptions<ServicePointLimits> limits) => limits.Value);
        app.MapGet("/api/config/area-limit", async (AreaLimit areaLimit, CancellationToken ct)
            => new { maxAreaKm2 = await areaLimit.GetMaxAreaKm2Async(ct) });
        return app;
    }
}
