namespace UniMap.ServicePoints.API.Here;

/// <summary>
/// Bound from <c>Here:ApiKey</c>, falling back to the <c>HERE_KEY</c> environment variable.
/// The key is optional at startup and checked when a HERE call is made.
/// </summary>
public class HereOptions
{
    public const string SectionName = "Here";
    public const string EnvironmentKeyName = "HERE_KEY";

    public string? ApiKey { get; set; }
}
