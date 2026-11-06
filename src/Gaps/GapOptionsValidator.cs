using Microsoft.Extensions.Options;

namespace UniMap.ServicePoints.API.Gaps;

/// <summary>Requires a finite, positive top speed.</summary>
public class GapOptionsValidator : IValidateOptions<GapOptions>
{
    public ValidateOptionsResult Validate(string? name, GapOptions options)
        => double.IsFinite(options.MaxSpeedKmh) && options.MaxSpeedKmh > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{GapOptions.SectionName}:{nameof(GapOptions.MaxSpeedKmh)} must be a finite number above 0 (got {options.MaxSpeedKmh}).");
}
