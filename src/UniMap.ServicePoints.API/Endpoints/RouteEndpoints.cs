using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Routes;

namespace UniMap.ServicePoints.API.Endpoints;

public static class RouteEndpoints
{
    public const string HereUnavailableMessage =
        "The HERE routing service is unavailable, so the route could not be calculated.";

    public const string NoRouteMessage = "HERE found no driving route between these service points.";

    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder app)
    {
        // A literal segment outranks a "{id:int}" route mapped later, so this path is never read as an id.
        app.MapGet("/api/service-points/route", async (HttpRequest request, RouteService routes, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!int.TryParse(request.Query["from"], out var from) || !int.TryParse(request.Query["to"], out var to))
                return Results.BadRequest(new { message = "Both 'from' and 'to' are required and must be whole-number service point ids." });
            if (from == to)
                return Results.BadRequest(new { message = "'from' and 'to' must be different service points." });

            try
            {
                var route = await routes.GetRouteAsync(from, to, ct);
                if (route is null)
                    return Results.NotFound(new { message = "Both service points must exist, be active and have a valid location." });

                return Results.Ok(new
                {
                    route.From,
                    route.To,
                    route.DistanceMeters,
                    route.DurationSeconds,
                    // GeoJSON: coordinates are [longitude, latitude].
                    Geometry = new
                    {
                        Type = "LineString",
                        Coordinates = route.Geometry.Coordinates.Select(c => new[] { c.X, c.Y }).ToArray(),
                    },
                    route.CalculatedAt,
                    route.FromCache,
                });
            }
            catch (HereNoRouteException)
            {
                return Results.Json(new { message = NoRouteMessage }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (HereUnavailableException ex)
            {
                // The reason stays in the log: it can name configuration keys. It never holds an API key or a URL.
                loggers.CreateLogger(nameof(RouteEndpoints)).LogWarning("Route failed because HERE failed: {Reason}", ex.Message);
                return Results.Json(new { message = HereUnavailableMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}
