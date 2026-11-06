using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.Gaps;

public interface IGapService
{
    /// <summary>
    /// Calculates the parts of the requested area that no eligible service point reaches within the thresholds.
    /// Data that is not sufficient gives <c>IsCalculated = false</c>. No gaps gives <c>IsCalculated = true</c> with an empty MultiPolygon.
    /// </summary>
    /// <exception cref="GapValidationException">The thresholds or the area are not usable.</exception>
    /// <exception cref="Here.HereUnavailableException">HERE failed for any point. There is no partial result.</exception>
    /// <exception cref="Isochrones.NoReachableAreaException">HERE found no reachable area from a point's location. There is no partial result.</exception>
    /// <exception cref="GapCalculationUnstableException">Points kept changing through every attempt. There is no partial result.</exception>
    Task<GapResult> CalculateAsync(GapRequest request, CancellationToken ct);
}
