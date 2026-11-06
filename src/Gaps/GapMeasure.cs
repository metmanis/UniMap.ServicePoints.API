namespace UniMap.ServicePoints.API.Gaps;

/// <summary>
/// The one definition of a gap piece's area. The 1 km2 rule and the exported AreaKm2 both use it,
/// so an exported area can never disagree with the rule.
/// </summary>
public static class GapMeasure
{
    /// <summary>
    /// SQL for the area of a polygon in square kilometres, measured along its drawn straight edges: a long thin piece
    /// has edges a few metres apart, so its vertices are every 0.01 degrees, where the bow of a great circle is about a centimetre.
    /// </summary>
    public static string SquareKmSql(string polygon) => $"ST_Area(ST_Segmentize({polygon}, 0.01)::geography) / 1000000.0";
}
