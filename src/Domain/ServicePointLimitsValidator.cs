using Microsoft.Extensions.Options;

namespace UniMap.ServicePoints.API.Domain;

/// <summary>Rejects missing, non-positive or inverted limits and names each offending key.</summary>
public class ServicePointLimitsValidator : IValidateOptions<ServicePointLimits>
{
    public ValidateOptionsResult Validate(string? name, ServicePointLimits options)
    {
        var failures = new List<string>();
        Check(failures, nameof(options.MinDriveTimeMinutes), options.MinDriveTimeMinutes);
        Check(failures, nameof(options.MaxDriveTimeMinutes), options.MaxDriveTimeMinutes);
        Check(failures, nameof(options.MinDistanceKm), options.MinDistanceKm);
        Check(failures, nameof(options.MaxDistanceKm), options.MaxDistanceKm);

        if (options.MaxDriveTimeMinutes < options.MinDriveTimeMinutes)
        {
            failures.Add($"{Key(nameof(options.MaxDriveTimeMinutes))} must not be below {nameof(options.MinDriveTimeMinutes)}.");
        }

        if (options.MaxDistanceKm < options.MinDistanceKm)
        {
            failures.Add($"{Key(nameof(options.MaxDistanceKm))} must not be below {nameof(options.MinDistanceKm)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Key(string property) => $"{ServicePointLimits.SectionName}:{property}";

    private static void Check(List<string> failures, string property, int value)
    {
        if (value <= 0)
        {
            failures.Add($"{Key(property)} is missing or not positive (got {value}).");
        }
    }
}
