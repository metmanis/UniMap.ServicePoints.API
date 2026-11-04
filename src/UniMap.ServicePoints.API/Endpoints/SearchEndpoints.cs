using UniMap.ServicePoints.API.Search;
using UniMap.ServicePoints.API.ServicePoints;

namespace UniMap.ServicePoints.API.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string? q, SearchQuery query, CancellationToken ct) =>
        {
            if ((q ?? "").Trim().Length > ServicePointQuery.MaxSearchLength)
                return Results.Problem(detail: $"q must be at most {ServicePointQuery.MaxSearchLength} characters.", statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await query.SearchAsync(q ?? "", ct));
        });

        return app;
    }
}
