using UniMap.ServicePoints.API.Distances;
using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.API.Endpoints;

public static class DistanceEndpoints
{
    public const string HereUnavailableMessage =
        "The HERE routing service is unavailable, so the driving distance could not be calculated.";

    public static IEndpointRouteBuilder MapDistanceEndpoints(this IEndpointRouteBuilder app)
    {
        // A literal segment outranks a "{id:int}" route mapped later, so this path is never read as an id.
        app.MapGet("/api/service-points/distance", async (HttpRequest request, DistanceService distances, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!int.TryParse(request.Query["from"], out var from) || !int.TryParse(request.Query["to"], out var to))
                return Results.BadRequest(new { message = "Both 'from' and 'to' are required and must be whole-number service point ids." });

            try
            {
                var km = await distances.GetDrivingDistanceKmAsync(from, to, ct);
                return km is null
                    ? Results.NotFound(new { message = "Both service points must exist, be active and have a valid location." })
                    : Results.Ok(new { from, to, distanceKm = km.Value });
            }
            catch (HereUnavailableException ex)
            {
                // The reason stays in the log: it can name configuration keys. It never holds an API key or a URL.
                loggers.CreateLogger(nameof(DistanceEndpoints)).LogWarning("Driving distance failed because HERE failed: {Reason}", ex.Message);
                return Results.Json(new { message = HereUnavailableMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}
