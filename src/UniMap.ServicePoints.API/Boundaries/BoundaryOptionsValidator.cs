using Microsoft.Extensions.Options;

namespace UniMap.ServicePoints.API.Boundaries;

/// <summary>Requires a finite, non-negative simplification tolerance.</summary>
public class BoundaryOptionsValidator : IValidateOptions<BoundaryOptions>
{
    public ValidateOptionsResult Validate(string? name, BoundaryOptions options)
        => double.IsFinite(options.SimplifyToleranceDegrees) && options.SimplifyToleranceDegrees >= 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{BoundaryOptions.ToleranceKey} must be a finite number >= 0 (got {options.SimplifyToleranceDegrees}).");
}
