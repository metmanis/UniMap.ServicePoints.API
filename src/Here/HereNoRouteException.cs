namespace UniMap.ServicePoints.API.Here;

/// <summary>
/// HERE answered normally, and the answer holds no route between the two points. This is not an outage, so it is
/// neither retried nor a <see cref="HereUnavailableException"/>.
/// </summary>
public class HereNoRouteException(string message) : Exception(message);
