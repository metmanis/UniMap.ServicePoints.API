using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Gaps;

/// <param name="Request">Null when <paramref name="Errors"/> has entries.</param>
public record GapRequestParseResult(GapRequest? Request, IReadOnlyList<string> Errors);

/// <summary>
/// Reads the body shared by the gap endpoints:
/// <c>{ "area": GeoJSON Polygon, "vehicle": "PassengerCar"|"HeavyDuty", "driveTimeMinutes": int?, "distanceKm": int? }</c>.
/// </summary>
public static class GapRequestParser
{
    /// <summary>The most positions an area may have, across all its rings. Far more than anyone draws by hand.</summary>
    public const int MaxAreaVertices = 5000;

    private const int Srid = 4326;

    /// <summary>Reads and writes GeoJSON geometries in EPSG:4326, with coordinates as longitude, latitude.</summary>
    public static readonly JsonSerializerOptions GeoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new GeoJsonConverterFactory(new GeometryFactory(new PrecisionModel(), Srid)) },
    };

    public static async Task<GapRequestParseResult> ParseAsync(Stream body, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new GapRequestParseResult(null, ["The request body is not valid JSON."]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new GapRequestParseResult(null, ["The request body must be a JSON object."]);

            var errors = new List<string>();
            var root = document.RootElement;
            var area = ReadArea(root, errors);
            var vehicle = ReadVehicle(root, errors);
            var minutes = ReadOptionalInt(root, "driveTimeMinutes", errors);
            var km = ReadOptionalInt(root, "distanceKm", errors);

            return errors.Count > 0
                ? new GapRequestParseResult(null, errors)
                : new GapRequestParseResult(new GapRequest(area!, vehicle!.Value, minutes, km), []);
        }
    }

    private static Polygon? ReadArea(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("area", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors.Add("area is required.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Polygon")
        {
            errors.Add("area must be a GeoJSON Polygon.");
            return null;
        }

        try
        {
            if (element.Deserialize<Geometry>(GeoJsonOptions) is Polygon polygon)
            {
                if (polygon.NumPoints > MaxAreaVertices)
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture, $"area has too many points: at most {MaxAreaVertices:N0} are allowed."));
                    return null;
                }

                polygon.SRID = Srid;
                return polygon;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            // The GeoJSON reader signals bad coordinates and rings in all of these ways.
        }

        errors.Add("area is not a valid GeoJSON Polygon: each ring needs at least four [longitude, latitude] positions and must be closed.");
        return null;
    }

    private static VehicleType? ReadVehicle(JsonElement root, List<string> errors)
    {
        var allowed = Enum.GetNames<VehicleType>();
        if (root.TryGetProperty("vehicle", out var element) && element.ValueKind == JsonValueKind.String
            && allowed.Contains(element.GetString()))
            return Enum.Parse<VehicleType>(element.GetString()!);

        errors.Add($"vehicle must be one of: {string.Join(", ", allowed)}.");
        return null;
    }

    private static int? ReadOptionalInt(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;

        errors.Add($"{name} must be a whole number.");
        return null;
    }
}
