using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.ServicePoints;
using UniMap.ServicePoints.API.Text;

namespace UniMap.ServicePoints.API.Search;

public class SearchQuery(NpgsqlDataSource dataSource)
{
    public const int MaxCities = 3, MaxDistricts = 3, MaxServicePoints = 10;
    private const int MinWordLength = 2;

    // Whether one query word matches a normalized field: contains it, is similar to it, or is a few edits from a
    // whole word in it. levenshtein raises an error beyond 255 characters; CASE guarantees the length check runs
    // first, which AND does not.
    private static string WordMatches(string field) => $"""
        ({field} LIKE '%' || w.escaped || '%' ESCAPE '\'
         OR word_similarity(w.word, {field}) >= @threshold
         OR (length(w.word) >= 3 AND EXISTS (
               SELECT 1 FROM regexp_split_to_table({field}, '\s+') part
               WHERE CASE WHEN length(part) <= 255
                          THEN levenshtein_less_equal(w.word, part, w.max_edits) <= w.max_edits END)))
        """;

    private static string EveryWordMatches(string condition) => $"""
        NOT EXISTS (SELECT 1 FROM words w WHERE NOT ({condition}))
        """;

    // How the whole query matches the result's name: the first and third ordering rules.
    private static string NameRanking(string name) => $"""
        CASE WHEN {name} = @query THEN 0
             WHEN {name} LIKE @prefix ESCAPE '\' THEN 1
             WHEN {name} LIKE @contains ESCAPE '\' THEN 2
             ELSE 3 END AS name_rank,
        similarity({name}, @query) AS name_similarity
        """;

    // Each kind takes its own best rows before the kinds are merged, so one kind cannot crowd out another.
    // brand_matches compares each word with each brand once: left to itself the planner repeats that for every
    // point, which took 470 ms on 2,000 points against 65 ms this way. A trigram index does not help that plan.
    private static readonly string Sql = $"""
        WITH words AS (SELECT word, escaped, CASE WHEN length(word) = 3 THEN 1 ELSE 2 END AS max_edits
                       FROM unnest(@words, @escaped) AS w(word, escaped)),
        brand_matches AS MATERIALIZED (
            SELECT w.word, b."Id" AS brand_id
            FROM words w
            JOIN "Brands"."Brands" b ON {WordMatches("b.\"NormalizedName\"")})
        SELECT kind, id, name, address, city, district, brands, latitude, longitude, has_location
        FROM (
            (SELECT 'city' AS kind, 0 AS kind_rank, c."Id" AS id, c."Name" AS name, NULL::text AS address,
                    NULL::text AS city, NULL::text AS district, NULL::text[] AS brands,
                    NULL::double precision AS latitude, NULL::double precision AS longitude, NULL::boolean AS has_location,
                    {NameRanking("n.name")}
             FROM "Administrative"."Cities" c
             CROSS JOIN LATERAL (SELECT "Services".normalize_text(c."Name") AS name) n
             WHERE {EveryWordMatches(WordMatches("n.name"))}
             ORDER BY name_rank, name_similarity DESC, c."Id"
             LIMIT @maxCities)
            UNION ALL
            (SELECT 'district', 1, d."Id", d."Name", NULL, parent."Name", NULL, NULL, NULL, NULL, NULL,
                    {NameRanking("n.name")}
             FROM "Administrative"."Districts" d
             LEFT JOIN "Administrative"."Cities" parent ON parent."Id" = d."CityId"
             CROSS JOIN LATERAL (SELECT "Services".normalize_text(d."Name") AS name) n
             WHERE {EveryWordMatches(WordMatches("n.name"))}
             ORDER BY name_rank, name_similarity DESC, d."Id"
             LIMIT @maxDistricts)
            UNION ALL
            (SELECT 'servicePoint', 2, sp."Id", sp."Name", sp."Address", place.city, place.district,
                    ARRAY(SELECT b."Name"
                          FROM "Services"."ServicePointsBrands" bsp
                          JOIN "Brands"."Brands" b ON b."Id" = bsp."BrandId"
                          WHERE bsp."ServicePointId" = sp."Id"),
                    CASE WHEN located.has_location THEN ST_Y(sp."Geometry") END,
                    CASE WHEN located.has_location THEN ST_X(sp."Geometry") END,
                    located.has_location,
                    {NameRanking("n.name")}
             FROM "Services"."ServicePoints" sp
             CROSS JOIN LATERAL (SELECT ({ServicePointQuery.ValidLocation}) IS TRUE AS has_location) located
             LEFT JOIN "Services"."ServicePointLocations" loc ON loc."ServicePointId" = sp."Id"
             LEFT JOIN "Administrative"."Cities" city ON city."Id" = loc."CityId"
             LEFT JOIN "Administrative"."Districts" district ON district."Id" = loc."DistrictId"
             CROSS JOIN LATERAL (SELECT CASE WHEN located.has_location THEN city."Name" ELSE sp."City" END AS city,
                                        CASE WHEN located.has_location THEN district."Name" ELSE sp."District" END AS district) place
             CROSS JOIN LATERAL (SELECT "Services".normalize_text(sp."Name") AS name,
                                        "Services".normalize_text(place.city) AS city,
                                        "Services".normalize_text(place.district) AS district) n
             WHERE {ServicePointQuery.Active}
               AND {EveryWordMatches($"""
                   {WordMatches("n.name")}
                   OR {WordMatches("n.city")}
                   OR {WordMatches("n.district")}
                   OR EXISTS (SELECT 1
                              FROM brand_matches bm
                              JOIN "Services"."ServicePointsBrands" bsp ON bsp."BrandId" = bm.brand_id
                              WHERE bm.word = w.word AND bsp."ServicePointId" = sp."Id")
                   """)}
             ORDER BY name_rank, name_similarity DESC, sp."Id"
             LIMIT @maxServicePoints)
        ) results
        ORDER BY name_rank, kind_rank, name_similarity DESC, id
        """;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken ct)
    {
        var parts = TurkishNormalizer.Normalize(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = parts.Where(p => p.Length >= MinWordLength).ToArray();
        if (words.Length == 0) return [];

        var query = string.Join(' ', parts);
        var escapedQuery = ServicePointQuery.EscapeLike(query);

        await using var cmd = dataSource.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("words", NpgsqlDbType.Array | NpgsqlDbType.Text, words);
        cmd.Parameters.AddWithValue("escaped", NpgsqlDbType.Array | NpgsqlDbType.Text, words.Select(ServicePointQuery.EscapeLike).ToArray());
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("prefix", escapedQuery + "%");
        cmd.Parameters.AddWithValue("contains", "%" + escapedQuery + "%");
        cmd.Parameters.AddWithValue("threshold", (float)ServicePointQuery.SimilarityThreshold);
        cmd.Parameters.AddWithValue("maxCities", MaxCities);
        cmd.Parameters.AddWithValue("maxDistricts", MaxDistricts);
        cmd.Parameters.AddWithValue("maxServicePoints", MaxServicePoints);

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(1);
            results.Add(reader.GetString(0) switch
            {
                "city" => new CitySearchResult(id, reader.GetString(2)),
                "district" => new DistrictSearchResult(id, reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4)),
                _ => new ServicePointSearchResult(
                    id,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetFieldValue<string[]>(6).Order(ServicePointQuery.NameOrder).ToArray(),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.GetBoolean(9)),
            });
        }
        return results;
    }
}
