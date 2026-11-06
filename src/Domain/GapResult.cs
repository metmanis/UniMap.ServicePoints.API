using NetTopologySuite.Geometries;

namespace UniMap.ServicePoints.API.Domain;

/// <param name="ExcludedPointCount">
/// Active points of the chosen vehicle type, nationwide, with no valid location. They could not be considered.
/// </param>
public record GapResult(bool IsCalculated, string? Message, MultiPolygon? Gaps, int ExcludedPointCount = 0);
