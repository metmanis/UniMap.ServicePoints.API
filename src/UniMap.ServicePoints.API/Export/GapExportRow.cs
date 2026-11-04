namespace UniMap.ServicePoints.API.Export;

/// <param name="WktGeometry">Longitude latitude, as stored.</param>
public record GapExportRow(int GapIndex, double AreaKm2, string Vehicle, int? DriveTimeMinutes, int? DistanceKm, string WktGeometry);
