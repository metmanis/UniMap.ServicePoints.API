namespace UniMap.ServicePoints.API.Here;

/// <summary>
/// HERE could not give a usable answer (retries exhausted, rejected request, or an incomplete response).
/// The message never contains the request URL, because the API key travels in the query string.
/// </summary>
public class HereUnavailableException(string message) : Exception(message);
