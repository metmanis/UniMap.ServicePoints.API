namespace UniMap.ServicePoints.API.Here;

/// <summary>
/// HERE answered normally, and the answer holds no reachable area for the origin. This is not an outage, so it is
/// neither retried nor a <see cref="HereUnavailableException"/>.
/// </summary>
public class HereEmptyIsolineException(string message) : Exception(message);
