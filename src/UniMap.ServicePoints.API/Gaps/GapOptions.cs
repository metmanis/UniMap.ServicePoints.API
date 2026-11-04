namespace UniMap.ServicePoints.API.Gaps;

/// <summary>Bound from the <c>Gaps</c> configuration section.</summary>
public class GapOptions
{
    public const string SectionName = "Gaps";

    public const double DefaultMaxSpeedKmh = 130;

    /// <summary>
    /// Top driving speed used to turn a time threshold into a straight-line search radius.
    /// It only chooses whose isochrones to fetch and is never part of a gap.
    /// </summary>
    public double MaxSpeedKmh { get; init; } = DefaultMaxSpeedKmh;
}
