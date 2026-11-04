namespace UniMap.ServicePoints.API.Domain;

public static class GapRequestValidator
{
    public static IReadOnlyList<string> Validate(GapRequest request, ServicePointLimits limits)
    {
        var errors = new List<string>();

        if (request.DriveTimeMinutes is null && request.DistanceKm is null)
        {
            errors.Add("At least one threshold (drive time or distance) must be set.");
        }

        if (request.DriveTimeMinutes is <= 0)
        {
            errors.Add("Drive time must be positive.");
        }
        else if (request.DriveTimeMinutes is { } minutes
            && (minutes < limits.MinDriveTimeMinutes || minutes > limits.MaxDriveTimeMinutes))
        {
            errors.Add($"Drive time must be between {limits.MinDriveTimeMinutes} and {limits.MaxDriveTimeMinutes} minutes.");
        }

        if (request.DistanceKm is <= 0)
        {
            errors.Add("Distance must be positive.");
        }
        else if (request.DistanceKm is { } km
            && (km < limits.MinDistanceKm || km > limits.MaxDistanceKm))
        {
            errors.Add($"Distance must be between {limits.MinDistanceKm} and {limits.MaxDistanceKm} km.");
        }

        return errors;
    }
}
