using NetTopologySuite.Geometries;

namespace UniMap.ServicePoints.API.Domain;

public record GapRequest(Polygon Area, VehicleType Vehicle, int? DriveTimeMinutes, int? DistanceKm);
