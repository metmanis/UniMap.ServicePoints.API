namespace UniMap.ServicePoints.API.Isochrones;

/// <summary>
/// HERE found no reachable area from a service point's location. A retry does not help, and leaving the point out
/// would report a false gap, so the calculation stops until the point's location is checked.
/// </summary>
public class NoReachableAreaException(int servicePointId)
    : Exception($"HERE found no reachable area from the location of service point {servicePointId}. Check that point's location.")
{
    public int ServicePointId { get; } = servicePointId;
}
