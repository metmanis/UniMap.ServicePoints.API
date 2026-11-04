namespace UniMap.ServicePoints.API.ServicePoints;

/// <summary>City and District are names from the coordinate-based ServicePointLocations view, not the text columns.</summary>
public record ServicePointSummary(
    int Id,
    string? Code,
    string? Name,
    string? Type,
    string? Address,
    string? City,
    string? District,
    IReadOnlyList<string> Brands,
    double Latitude,
    double Longitude);

public record ServicePointSuggestion(
    int Id,
    string? Code,
    string? Name,
    string? Type,
    string? Address,
    string? City,
    string? District,
    IReadOnlyList<string> Brands,
    double? Latitude,
    double? Longitude,
    bool HasLocation);

/// <summary>Everything the details drawer shows. Without a valid location the coordinates are null and City and District come from the text columns.</summary>
public record ServicePointDetail(
    int Id,
    long? Code,
    string? Name,
    string? Type,
    string? Address,
    string? City,
    string? District,
    string? Phone,
    IReadOnlyList<string> Brands,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<string> VehicleTypes,
    bool IsStale,
    bool HasLocation);

public record NamedId(int Id, string Name);

public record FilterOptions(
    IReadOnlyList<NamedId> Cities,
    IReadOnlyList<NamedId> Districts,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> VehicleTypes);
