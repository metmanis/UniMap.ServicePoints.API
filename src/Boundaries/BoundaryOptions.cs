namespace UniMap.ServicePoints.API.Boundaries;

/// <summary>Bound from the <c>Boundaries</c> configuration section.</summary>
public class BoundaryOptions
{
    public const string SectionName = "Boundaries";

    /// <summary>Key as written in configuration, used in validation messages.</summary>
    public const string ToleranceKey = $"{SectionName}:{nameof(SimplifyToleranceDegrees)}";

    public const double DefaultToleranceDegrees = 0.0005;

    public double SimplifyToleranceDegrees { get; init; } = DefaultToleranceDegrees;
}
