namespace UniMap.ServicePoints.API.Domain;

/// <summary>Threshold limits, bound from the <c>ServicePointLimits</c> configuration section.</summary>
public record ServicePointLimits(
    int MinDriveTimeMinutes,
    int MaxDriveTimeMinutes,
    int MinDistanceKm,
    int MaxDistanceKm)
{
    // The options system creates the type with Activator before binding, which needs a
    // parameterless constructor. Positional properties are init-only and are set by the binder.
    // Missing keys stay 0 and are rejected by ServicePointLimitsValidator.
    public ServicePointLimits() : this(0, 0, 0, 0)
    {
    }

    public const string SectionName = "ServicePointLimits";

    /// <summary>Gap pieces smaller than this are dropped (design 8.7).</summary>
    public const double MinGapSquareKm = 1.0;
}
