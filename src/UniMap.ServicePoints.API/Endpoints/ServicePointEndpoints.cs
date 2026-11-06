using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.ServicePoints;

namespace UniMap.ServicePoints.API.Endpoints;

public static class ServicePointEndpoints
{
    private const int SuggestionLimit = 10;

    public static IEndpointRouteBuilder MapServicePointEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/service-points");

        group.MapGet("", async (HttpRequest request, ServicePointQuery query, CancellationToken ct) =>
        {
            if (!TryParseFilter(request.Query, out var filter, out var error))
                return Results.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await query.FilterAsync(filter, ct));
        });

        group.MapGet("suggestions", async (string? q, ServicePointQuery query, CancellationToken ct) =>
        {
            if ((q ?? "").Trim().Length > ServicePointQuery.MaxSearchLength)
                return Results.Problem(detail: $"q must be at most {ServicePointQuery.MaxSearchLength} characters.", statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await query.SearchByNameAsync(q ?? "", SuggestionLimit, ct));
        });

        group.MapGet("filter-options", (ServicePointQuery query, CancellationToken ct)
            => query.GetFilterOptionsAsync(ct));

        // The int constraint keeps the fixed routes above (and /distance) from being read as ids.
        group.MapGet("{id:int}", async (int id, ServicePointQuery query, CancellationToken ct)
            => await query.GetDetailAsync(id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        return app;
    }

    private static bool TryParseFilter(IQueryCollection query, out ServicePointFilter filter, out string error)
    {
        filter = new ServicePointFilter(null, null, null, null, null);
        error = "";

        if (!TryParseInts(query, "cityIds", out var cityIds, out error)) return false;
        if (!TryParseInts(query, "districtIds", out var districtIds, out error)) return false;

        var vehicleTypes = new List<VehicleType>();
        foreach (var value in query["vehicleTypes"])
        {
            var name = Enum.GetNames<VehicleType>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                error = $"Unknown vehicleTypes value '{value}'. Allowed: {string.Join(", ", Enum.GetNames<VehicleType>())}.";
                return false;
            }
            vehicleTypes.Add(Enum.Parse<VehicleType>(name));
        }

        filter = new ServicePointFilter(
            cityIds, districtIds,
            query["brands"].OfType<string>().ToList(),
            query["types"].OfType<string>().ToList(),
            vehicleTypes);
        return true;
    }

    private static bool TryParseInts(IQueryCollection query, string key, out List<int> ids, out string error)
    {
        ids = [];
        error = "";
        foreach (var value in query[key])
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                error = $"Invalid {key} value '{value}': expected an integer.";
                return false;
            }
            ids.Add(id);
        }
        return true;
    }
}
