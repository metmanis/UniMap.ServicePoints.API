using System.Text.Json.Serialization;

namespace UniMap.ServicePoints.API.Search;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ServicePointSearchResult), "servicePoint")]
[JsonDerivedType(typeof(CitySearchResult), "city")]
[JsonDerivedType(typeof(DistrictSearchResult), "district")]
public abstract record SearchResult(int Id);

/// <summary>Without a valid location the coordinates are null and City and District come from the text columns.</summary>
public record ServicePointSearchResult(
    int Id,
    string? Name,
    string? Address,
    string? City,
    string? District,
    IReadOnlyList<string> Brands,
    double? Latitude,
    double? Longitude,
    bool HasLocation) : SearchResult(Id);

public record CitySearchResult(int Id, string Name) : SearchResult(Id);

/// <param name="CityName">Null for a district that lies in no city.</param>
public record DistrictSearchResult(int Id, string Name, string? CityName) : SearchResult(Id);
