using UniMap.ServicePoints.API.Boundaries;

namespace UniMap.ServicePoints.API.Endpoints;

public static class BoundaryEndpoints
{
    public static IEndpointRouteBuilder MapBoundaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/boundaries/{kind}/{id:int}", async (string kind, int id, BoundaryQuery query, CancellationToken ct) =>
        {
            // Match by name only: Enum.TryParse would also accept numeric strings such as "0".
            var name = Enum.GetNames<BoundaryKind>().FirstOrDefault(n => string.Equals(n, kind, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return Results.Problem(
                    detail: $"Unknown boundary kind '{kind}'. Allowed: city, district.",
                    statusCode: StatusCodes.Status400BadRequest);

            var geoJson = await query.GetBoundaryGeoJsonAsync(Enum.Parse<BoundaryKind>(name), id, ct);
            return geoJson is null
                ? Results.NotFound()
                : Results.Content(geoJson, "application/geo+json");
        });

        return app;
    }
}
