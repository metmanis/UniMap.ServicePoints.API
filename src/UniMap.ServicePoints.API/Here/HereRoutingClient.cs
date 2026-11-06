using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Here;

public class HereRoutingClient(
    HttpClient http,
    IOptions<HereOptions> options,
    IHereDelay delay,
    ILogger<HereRoutingClient> logger) : IHereRoutingClient
{
    private const string IsolineUrl = "https://isoline.router.hereapi.com/v8/isolines";
    private const string MatrixUrl = "https://matrix.router.hereapi.com/v8/matrix";
    private const string RouteUrl = "https://router.hereapi.com/v8/routes";
    private const int MaxAttempts = 4; // 1 initial + 3 retries, backoff 1 s, 2 s, 4 s

    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    public async Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
        Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(rangeValues);
        if (rangeValues.Count == 0) throw new ArgumentException("At least one range value is required.", nameof(rangeValues));

        var key = RequireKey();
        var rangeTypeName = rangeType == RangeType.Time ? "time" : "distance";
        var values = string.Join(',', rangeValues.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        // Coordinate.Y is latitude, X is longitude; HERE wants lat,lng.
        var url = $"{IsolineUrl}?transportMode={(vehicle == VehicleType.HeavyDuty ? "truck" : "car")}" +
                  $"&origin={Num(origin.Y)},{Num(origin.X)}" +
                  $"&range[type]={rangeTypeName}&range[values]={values}";
        if (vehicle == VehicleType.HeavyDuty)
            url += "&vehicle[grossWeight]=12000&vehicle[height]=350&vehicle[width]=250&vehicle[length]=900";
        url += $"&apiKey={Uri.EscapeDataString(key)}";

        logger.LogInformation("HERE isoline request: vehicle {Vehicle}, range type {RangeType}, values {Values}",
            vehicle, rangeType, values);

        using var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return ParseWellFormed(() => ParseIsolines(json.RootElement, rangeType, rangeValues));
    }

    public async Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var key = RequireKey();
        var body = new
        {
            origins = new[] { new { lat = from.Y, lng = from.X } },
            destinations = new[] { new { lat = to.Y, lng = to.X } },
            regionDefinition = new { type = "world" },
            transportMode = "car",
            matrixAttributes = new[] { "distances" },
        };
        var url = $"{MatrixUrl}?apiKey={Uri.EscapeDataString(key)}&async=false";

        logger.LogInformation("HERE matrix request: vehicle {Vehicle}, attribute distances", VehicleType.PassengerCar);

        using var json = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) }, ct);
        return ParseWellFormed(() => ParseDistanceKm(json.RootElement));
    }

    public async Task<DrivingRoute> GetDrivingRouteAsync(Coordinate from, Coordinate to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var key = RequireKey();
        // Coordinate.Y is latitude, X is longitude; HERE wants lat,lng. departureTime=any asks for no live traffic.
        var url = $"{RouteUrl}?transportMode=car&origin={Num(from.Y)},{Num(from.X)}&destination={Num(to.Y)},{Num(to.X)}" +
                  $"&return=polyline,summary&departureTime=any&apiKey={Uri.EscapeDataString(key)}";

        logger.LogInformation("HERE route request: vehicle {Vehicle}", VehicleType.PassengerCar);

        using var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return ParseWellFormed(() => ParseRoute(json.RootElement));
    }

    private string RequireKey()
    {
        var key = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new HereUnavailableException(
                $"HERE API key is not configured. Set {HereOptions.SectionName}:ApiKey or the {HereOptions.EnvironmentKeyName} environment variable.");
        return key;
    }

    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            string failure;
            try
            {
                using var request = createRequest();
                using var response = await http.SendAsync(request, ct);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    }
                    catch (JsonException)
                    {
                        throw new HereUnavailableException("HERE returned a response that is not valid JSON.");
                    }
                }

                if (response.StatusCode != HttpStatusCode.TooManyRequests && status < 500)
                    throw new HereUnavailableException($"HERE rejected the request with status {status}.");

                failure = $"status {status}";
            }
            catch (HttpRequestException ex)
            {
                failure = $"network error ({ex.GetType().Name})";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = "timeout";
            }

            if (attempt >= MaxAttempts)
            {
                logger.LogError("HERE request failed after {Attempts} attempts: {Failure}", attempt, failure);
                throw new HereUnavailableException($"HERE is unavailable: {failure} after {attempt} attempts.");
            }

            var wait = TimeSpan.FromSeconds(1 << (attempt - 1));
            logger.LogWarning("HERE request attempt {Attempt} failed ({Failure}); retrying in {Wait}", attempt, failure, wait);
            await delay.DelayAsync(wait, ct);
        }
    }

    // JsonElement throws these when a value has an unexpected JSON kind or number form.
    private static T ParseWellFormed<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new HereUnavailableException("HERE returned a response with an unexpected structure.");
        }
    }

    private static IReadOnlyList<Isoline> ParseIsolines(JsonElement root, RangeType rangeType, IReadOnlyList<int> requested)
    {
        if (!root.TryGetProperty("isolines", out var isolines) || isolines.ValueKind != JsonValueKind.Array)
            throw new HereUnavailableException("HERE returned no isolines.");
        if (isolines.GetArrayLength() == 0)
            throw new HereEmptyIsolineException("HERE returned an empty list of isolines.");

        var byValue = new Dictionary<int, MultiPolygon>();
        foreach (var isoline in isolines.EnumerateArray())
        {
            if (!isoline.TryGetProperty("range", out var range) ||
                !range.TryGetProperty("value", out var valueElement) ||
                !valueElement.TryGetInt32(out var value))
                continue;
            byValue[value] = ParseMultiPolygon(isoline);
        }

        var result = new List<Isoline>(requested.Count);
        foreach (var value in requested)
        {
            if (!byValue.TryGetValue(value, out var geometry))
                throw new HereUnavailableException($"HERE returned no isoline for {rangeType} value {value}.");
            result.Add(new Isoline(value, geometry));
        }
        return result;
    }

    private static MultiPolygon ParseMultiPolygon(JsonElement isoline)
    {
        if (!isoline.TryGetProperty("polygons", out var polygons) || polygons.ValueKind != JsonValueKind.Array)
            throw new HereUnavailableException("HERE returned an isoline without polygons.");
        if (polygons.GetArrayLength() == 0)
            throw new HereEmptyIsolineException("HERE returned an isoline with an empty list of polygons.");

        try
        {
            var result = new List<Polygon>();
            foreach (var polygon in polygons.EnumerateArray())
            {
                var outer = polygon.TryGetProperty("outer", out var o) ? o.GetString() : null;
                if (string.IsNullOrEmpty(outer))
                    throw new HereUnavailableException("HERE returned a polygon without an outer ring.");

                var holes = new List<LinearRing>();
                // A hole read as absent would count unreachable land as reached, so a bad value must fail.
                if (polygon.TryGetProperty("inner", out var inner) && inner.ValueKind != JsonValueKind.Null)
                    holes.AddRange(inner.EnumerateArray().Select(h => ToRing(h.GetString())));

                result.Add(Factory.CreatePolygon(ToRing(outer), holes.ToArray()));
            }
            return Factory.CreateMultiPolygon(result.ToArray());
        }
        catch (FormatException)
        {
            throw new HereUnavailableException("HERE returned a polygon that could not be decoded.");
        }
        catch (ArgumentException)
        {
            throw new HereUnavailableException("HERE returned a polygon that is not a valid ring.");
        }
    }

    private static LinearRing ToRing(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) throw new FormatException("Empty ring.");
        // The decoder yields (lat, lng); coordinates are x = longitude, y = latitude.
        var coordinates = FlexiblePolyline.Decode(encoded).Select(p => new Coordinate(p.Lng, p.Lat)).ToList();
        if (coordinates.Count == 0) throw new FormatException("A polyline with a header and no points.");
        if (!coordinates[0].Equals2D(coordinates[^1]))
            coordinates.Add(coordinates[0].Copy());
        return Factory.CreateLinearRing(coordinates.ToArray());
    }

    // Reads only each section's polyline, length and duration; everything else in the response is dropped.
    private static DrivingRoute ParseRoute(JsonElement root)
    {
        if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
            throw new HereUnavailableException("HERE returned no routes.");
        if (routes.GetArrayLength() == 0)
            throw new HereNoRouteException("HERE returned an empty list of routes.");

        if (!routes[0].TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array ||
            sections.GetArrayLength() == 0)
            throw new HereUnavailableException("HERE returned a route without sections.");

        var points = new List<Coordinate>();
        double length = 0;
        long duration = 0;
        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("polyline", out var polyline) || polyline.ValueKind != JsonValueKind.String)
                throw new HereUnavailableException("HERE returned a route section without a polyline.");
            if (!section.TryGetProperty("summary", out var summary) ||
                !summary.TryGetProperty("length", out var lengthElement) ||
                !summary.TryGetProperty("duration", out var durationElement))
                throw new HereUnavailableException("HERE returned a route section without a summary.");

            var sectionLength = lengthElement.GetDouble();
            var sectionDuration = durationElement.GetInt64();
            if (!double.IsFinite(sectionLength) || sectionLength < 0 || sectionDuration < 0)
                throw new HereUnavailableException("HERE returned a route section with a negative or invalid length or duration.");
            length += sectionLength;
            duration += sectionDuration;

            IReadOnlyList<(double Lat, double Lng)> decoded;
            try
            {
                decoded = FlexiblePolyline.Decode(polyline.GetString()!);
            }
            catch (FormatException)
            {
                throw new HereUnavailableException("HERE returned a route polyline that could not be decoded.");
            }

            // The decoder yields (lat, lng); coordinates are x = longitude, y = latitude.
            for (var i = 0; i < decoded.Count; i++)
            {
                var point = new Coordinate(decoded[i].Lng, decoded[i].Lat);
                // Consecutive sections share their junction point; keep it once.
                if (i == 0 && points.Count > 0 && points[^1].Equals2D(point)) continue;
                points.Add(point);
            }
        }

        if (points.Count < 2 || !double.IsFinite(length) || duration > int.MaxValue)
            throw new HereUnavailableException("HERE returned a route that is not a usable line.");

        return new DrivingRoute(Factory.CreateLineString(points.ToArray()), length, (int)duration);
    }

    private static double ParseDistanceKm(JsonElement root)
    {
        if (!root.TryGetProperty("matrix", out var matrix))
            throw new HereUnavailableException("HERE returned no matrix.");

        if (matrix.TryGetProperty("errorCodes", out var errors) && errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0 && errors[0].ValueKind == JsonValueKind.Number && errors[0].GetInt32() != 0)
            throw new HereUnavailableException($"HERE could not route between the points (error code {errors[0].GetInt32()}).");

        if (!matrix.TryGetProperty("distances", out var distances) || distances.ValueKind != JsonValueKind.Array ||
            distances.GetArrayLength() == 0 || distances[0].ValueKind != JsonValueKind.Number)
            throw new HereUnavailableException("HERE returned no distance.");

        return distances[0].GetDouble() / 1000.0;
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
