using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Text;

namespace UniMap.ServicePoints.API.ServicePoints;

public class ServicePointQuery(NpgsqlDataSource dataSource)
{
    public const double SimilarityThreshold = 0.3;
    private const int MinSearchLength = 2;

    /// <summary>The longest name search accepted, after trimming. Longer text costs more to compare and names no point.</summary>
    public const int MaxSearchLength = 100;

    // Brand names sort in Turkish order; the comparer falls back to invariant when the culture is unavailable.
    private static readonly StringComparer NameOrder = StringComparer.Create(new CultureInfo("tr-TR"), ignoreCase: false);

    // Active means IsActive IS TRUE (null is not active).
    internal const string Active = """sp."IsActive" IS TRUE""";

    // The one definition of a valid location: a point that can be placed on the map and measured from. Markers,
    // sufficiency, candidates and distance all reach it through Visible. Evaluated alone it can be NULL, so use IS TRUE.
    internal const string ValidLocation = """
        sp."Geometry" IS NOT NULL AND ST_IsValid(sp."Geometry") AND NOT ST_IsEmpty(sp."Geometry")
        AND ST_X(sp."Geometry") BETWEEN -180 AND 180 AND ST_Y(sp."Geometry") BETWEEN -90 AND 90
        """;

    // Active with a valid location: what the map, the gap calculation and the distance work on.
    internal const string Visible = $"{Active} AND {ValidLocation}";

    private const string SelectSummary = """
        SELECT sp."Id", sp."Code"::text, sp."Name", sp."Type", sp."Address",
               city."Name", district."Name",
               ARRAY(SELECT b."Name"
                     FROM "Services"."ServicePointsBrands" bsp
                     JOIN "Brands"."Brands" b ON b."Id" = bsp."BrandId"
                     WHERE bsp."ServicePointId" = sp."Id"),
               ST_Y(sp."Geometry"), ST_X(sp."Geometry")
        FROM "Services"."ServicePoints" sp
        LEFT JOIN "Services"."ServicePointLocations" loc ON loc."ServicePointId" = sp."Id"
        LEFT JOIN "Administrative"."Cities" city ON city."Id" = loc."CityId"
        LEFT JOIN "Administrative"."Districts" district ON district."Id" = loc."DistrictId"
        """;

    public async Task<IReadOnlyList<ServicePointSummary>> FilterAsync(ServicePointFilter filter, CancellationToken ct)
    {
        var sql = new System.Text.StringBuilder(SelectSummary).Append("\nWHERE ").Append(Visible);
        await using var cmd = dataSource.CreateCommand();

        if (filter.CityIds is { Count: > 0 })
        {
            sql.Append("\n  AND loc.\"CityId\" = ANY (@cityIds)");
            cmd.Parameters.AddWithValue("cityIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, filter.CityIds.ToArray());
        }
        if (filter.DistrictIds is { Count: > 0 })
        {
            sql.Append("\n  AND loc.\"DistrictId\" = ANY (@districtIds)");
            cmd.Parameters.AddWithValue("districtIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, filter.DistrictIds.ToArray());
        }

        var brands = (filter.Brands ?? [])
            .Select(TurkishNormalizer.Normalize).Where(b => b.Length > 0).Distinct().ToArray();
        if (brands.Length > 0)
        {
            sql.Append("""

                  AND EXISTS (SELECT 1 FROM "Services"."ServicePointsBrands" fb
                              JOIN "Brands"."Brands" fbb ON fbb."Id" = fb."BrandId"
                              WHERE fb."ServicePointId" = sp."Id" AND fbb."NormalizedName" = ANY (@brands))
                """);
            cmd.Parameters.AddWithValue("brands", NpgsqlDbType.Array | NpgsqlDbType.Text, brands);
        }
        if (filter.Types is { Count: > 0 })
        {
            sql.Append("\n  AND sp.\"Type\" = ANY (@types)");
            cmd.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text, filter.Types.ToArray());
        }
        if (filter.VehicleTypes is { Count: > 0 })
        {
            sql.Append("""

                  AND EXISTS (SELECT 1 FROM "Services"."ServicePointVehicleTypes" fv
                              WHERE fv."ServicePointId" = sp."Id" AND fv."VehicleType" = ANY (@vehicleTypes))
                """);
            cmd.Parameters.AddWithValue("vehicleTypes", NpgsqlDbType.Array | NpgsqlDbType.Text,
                filter.VehicleTypes.Select(v => v.ToString()).ToArray());
        }
        sql.Append("\nORDER BY sp.\"Id\"");

        cmd.CommandText = sql.ToString();
        return await ReadSummariesAsync(cmd, ct);
    }

    // Suggestions cover every active point, also those with no valid location. Without coordinates there is
    // nothing to derive the city and district from, so those two come from the text columns.
    public async Task<IReadOnlyList<ServicePointSuggestion>> SearchByNameAsync(string text, int limit, CancellationToken ct)
    {
        var normalized = TurkishNormalizer.Normalize(text.Trim());
        if (normalized.Length < MinSearchLength || limit <= 0) return [];

        // LIKE patterns: the user's text is escaped so %, _ and \ match literally.
        var escaped = normalized.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

        await using var cmd = dataSource.CreateCommand($"""
            SELECT sp."Id", sp."Code"::text, sp."Name", sp."Type", sp."Address",
                   CASE WHEN located.has_location THEN city."Name" ELSE sp."City" END,
                   CASE WHEN located.has_location THEN district."Name" ELSE sp."District" END,
                   ARRAY(SELECT b."Name"
                         FROM "Services"."ServicePointsBrands" bsp
                         JOIN "Brands"."Brands" b ON b."Id" = bsp."BrandId"
                         WHERE bsp."ServicePointId" = sp."Id"),
                   CASE WHEN located.has_location THEN ST_Y(sp."Geometry") END,
                   CASE WHEN located.has_location THEN ST_X(sp."Geometry") END,
                   located.has_location
            FROM "Services"."ServicePoints" sp
            CROSS JOIN LATERAL (SELECT ({ValidLocation}) IS TRUE AS has_location) located
            LEFT JOIN "Services"."ServicePointLocations" loc ON loc."ServicePointId" = sp."Id"
            LEFT JOIN "Administrative"."Cities" city ON city."Id" = loc."CityId"
            LEFT JOIN "Administrative"."Districts" district ON district."Id" = loc."DistrictId"
            WHERE {Active}
              AND ("Services".normalize_text(sp."Name") LIKE @contains ESCAPE '\'
                   OR similarity("Services".normalize_text(sp."Name"), @query) >= @threshold)
            ORDER BY CASE WHEN "Services".normalize_text(sp."Name") LIKE @prefix ESCAPE '\' THEN 0
                          WHEN "Services".normalize_text(sp."Name") LIKE @contains ESCAPE '\' THEN 1
                          ELSE 2 END,
                     similarity("Services".normalize_text(sp."Name"), @query) DESC,
                     sp."Id"
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("query", normalized);
        cmd.Parameters.AddWithValue("contains", "%" + escaped + "%");
        cmd.Parameters.AddWithValue("prefix", escaped + "%");
        cmd.Parameters.AddWithValue("threshold", (float)SimilarityThreshold);
        cmd.Parameters.AddWithValue("limit", limit);

        var result = new List<ServicePointSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ServicePointSuggestion(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<string[]>(7).Order(NameOrder).ToArray(),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.GetBoolean(10)));
        }
        return result;
    }

    // Any active point, with or without a valid location. Inactive and unknown ids return null.
    public async Task<ServicePointDetail?> GetDetailAsync(int id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT sp."Id", sp."Code", sp."Name", sp."Type", sp."Address",
                   CASE WHEN located.has_location THEN city."Name" ELSE sp."City" END,
                   CASE WHEN located.has_location THEN district."Name" ELSE sp."District" END,
                   sp."Phone",
                   ARRAY(SELECT b."Name"
                         FROM "Services"."ServicePointsBrands" bsp
                         JOIN "Brands"."Brands" b ON b."Id" = bsp."BrandId"
                         WHERE bsp."ServicePointId" = sp."Id"),
                   CASE WHEN located.has_location THEN ST_Y(sp."Geometry") END,
                   CASE WHEN located.has_location THEN ST_X(sp."Geometry") END,
                   ARRAY(SELECT v."VehicleType"::text
                         FROM "Services"."ServicePointVehicleTypes" v
                         WHERE v."ServicePointId" = sp."Id"),
                   sp."IsStale",
                   located.has_location
            FROM "Services"."ServicePoints" sp
            CROSS JOIN LATERAL (SELECT ({ValidLocation}) IS TRUE AS has_location) located
            LEFT JOIN "Services"."ServicePointLocations" loc ON loc."ServicePointId" = sp."Id"
            LEFT JOIN "Administrative"."Cities" city ON city."Id" = loc."CityId"
            LEFT JOIN "Administrative"."Districts" district ON district."Id" = loc."DistrictId"
            WHERE sp."Id" = @id AND {Active}
            """);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ServicePointDetail(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetFieldValue<string[]>(8).Order(NameOrder).ToArray(),
            reader.IsDBNull(9) ? null : reader.GetDouble(9),
            reader.IsDBNull(10) ? null : reader.GetDouble(10),
            reader.GetFieldValue<string[]>(11).Order(NameOrder).ToArray(),
            reader.GetBoolean(12),
            reader.GetBoolean(13));
    }

    public async Task<FilterOptions> GetFilterOptionsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT 'city', "Id", "Name", NULL::integer FROM "Administrative"."Cities" WHERE "Name" IS NOT NULL
            UNION ALL
            SELECT 'district', "Id", "Name", "CityId" FROM "Administrative"."Districts" WHERE "Name" IS NOT NULL
            UNION ALL
            SELECT 'brand', 0, "Name", NULL FROM "Brands"."Brands"
            UNION ALL
            SELECT DISTINCT 'type', 0, sp."Type", NULL::integer FROM "Services"."ServicePoints" sp
            WHERE {Visible} AND sp."Type" IS NOT NULL
            """);

        List<NamedId> cities = [];
        List<DistrictOption> districts = [];
        List<string> brands = [], types = [];
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(2);
            switch (reader.GetString(0))
            {
                case "city": cities.Add(new NamedId(reader.GetInt32(1), name)); break;
                case "district": districts.Add(new DistrictOption(reader.GetInt32(1), name, reader.IsDBNull(3) ? null : reader.GetInt32(3))); break;
                case "brand": brands.Add(name); break;
                default: types.Add(name); break;
            }
        }

        return new FilterOptions(
            cities.OrderBy(c => c.Name, NameOrder).ThenBy(c => c.Id).ToList(),
            districts.OrderBy(d => d.Name, NameOrder).ThenBy(d => d.Id).ToList(),
            brands.Order(NameOrder).ToList(),
            types.Order(NameOrder).ToList(),
            Enum.GetNames<VehicleType>().Order(NameOrder).ToList());
    }

    private static async Task<IReadOnlyList<ServicePointSummary>> ReadSummariesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<ServicePointSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ServicePointSummary(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<string[]>(7).Order(NameOrder).ToArray(),
                reader.GetDouble(8),
                reader.GetDouble(9)));
        }
        return result;
    }
}
