using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Here;

/// <summary>One reachable area for one range value (SRID 4326, x = longitude).</summary>
public record Isoline(int RangeValue, MultiPolygon Geometry);

/// <summary>Only what the app keeps from a HERE route. SRID 4326, x = longitude.</summary>
public record DrivingRoute(LineString Geometry, double DistanceMeters, int DurationSeconds);

/// <summary>The only gateway to HERE. Server-side only; the key never leaves the server.</summary>
public interface IHereRoutingClient
{
    /// <param name="rangeValues">Seconds for <see cref="RangeType.Time"/>, metres for <see cref="RangeType.Distance"/>.</param>
    /// <returns>One isoline per requested value, in request order. Never empty.</returns>
    /// <exception cref="HereUnavailableException">HERE failed or returned an incomplete answer.</exception>
    Task<IReadOnlyList<Isoline>> GetIsolinesAsync(
        Coordinate origin, VehicleType vehicle, RangeType rangeType, IReadOnlyList<int> rangeValues, CancellationToken ct);

    /// <summary>Driving distance in kilometres (cars only).</summary>
    /// <exception cref="HereUnavailableException">HERE failed or returned no distance.</exception>
    Task<double> GetDrivingDistanceKmAsync(Coordinate from, Coordinate to, CancellationToken ct);

    /// <summary>Car route between two points, all sections joined into one line.</summary>
    /// <exception cref="HereNoRouteException">HERE found no route.</exception>
    /// <exception cref="HereUnavailableException">HERE failed or returned an incomplete or malformed answer.</exception>
    Task<DrivingRoute> GetDrivingRouteAsync(Coordinate from, Coordinate to, CancellationToken ct);
}
