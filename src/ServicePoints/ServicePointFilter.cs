using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.API.ServicePoints;

/// <summary>AND across fields, OR within a field. A null or empty list means the field is not filtered.</summary>
public record ServicePointFilter(
    IReadOnlyList<int>? CityIds,
    IReadOnlyList<int>? DistrictIds,
    IReadOnlyList<string>? Brands,
    IReadOnlyList<string>? Types,
    IReadOnlyList<VehicleType>? VehicleTypes);
