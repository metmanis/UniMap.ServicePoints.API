using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Isochrones;

public record ServicePointCoverageTarget(int ServicePointId, Coordinate Location);

public record ServicePointIsochrone(int ServicePointId, RangeType RangeType, int RangeValue, MultiPolygon Geometry);

public interface IIsochroneService
{
    /// <summary>
    /// Returns one drivable-reach polygon per distinct point, in input order. Each exact
    /// (point, vehicle, range type, value) is its own cache row; a smaller range is never trimmed from a larger one.
    /// A point that moved or went away during its HERE call stores no row and is left out of the result.
    /// </summary>
    /// <param name="rangeValue">Seconds for <see cref="RangeType.Time"/>, metres for <see cref="RangeType.Distance"/>.</param>
    /// <exception cref="Here.HereUnavailableException">
    /// HERE failed for a point. Rows already stored for points that succeeded stay.
    /// </exception>
    /// <exception cref="NoReachableAreaException">HERE answered with no reachable area for a point. Rows stored for others stay.</exception>
    Task<IReadOnlyList<ServicePointIsochrone>> GetOrComputeAsync(
        IReadOnlyList<ServicePointCoverageTarget> points, VehicleType vehicle, RangeType rangeType, int rangeValue,
        CancellationToken ct);
}
