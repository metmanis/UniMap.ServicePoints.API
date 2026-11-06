namespace UniMap.ServicePoints.API.Gaps;

/// <summary>Service points kept changing during the calculation, so every attempt ended with a reach missing.</summary>
public class GapCalculationUnstableException()
    : Exception("Service points kept changing during the calculation, so no result was produced. Try again.");
