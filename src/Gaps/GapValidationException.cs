namespace UniMap.ServicePoints.API.Gaps;

/// <summary>The request cannot be calculated as given. <see cref="Errors"/> holds messages fit to show the user.</summary>
public class GapValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
