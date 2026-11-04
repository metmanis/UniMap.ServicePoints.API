using System.Globalization;
using Npgsql;

namespace UniMap.ServicePoints.Tests.Support;

/// <summary>Raw-SQL seeding helpers for tests that need service points, boundaries, brands and vehicle types.</summary>
public class ServicePointSeed(ScratchDatabase db)
{
    public async Task Exec(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    public Task Clear() => Exec("""
        DELETE FROM "Services"."ServicePoints";
        DELETE FROM "Brands"."Brands";
        DELETE FROM "Administrative"."Districts";
        DELETE FROM "Administrative"."Cities";
        """);

    private static string Rect(double x1, double y1, double x2, double y2)
        => $"ST_Multi(ST_MakeEnvelope({Num(x1)}, {Num(y1)}, {Num(x2)}, {Num(y2)}, 4326))";

    public Task City(int id, string name, double x1, double y1, double x2, double y2) => Exec(
        $"""INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry") VALUES ({id}, '{name}', {Rect(x1, y1, x2, y2)})""");

    public Task District(int id, string name, double x1, double y1, double x2, double y2, int? cityId = null) => Exec(
        $"""INSERT INTO "Administrative"."Districts" ("Id", "Name", "Geometry", "CityId") VALUES ({id}, '{name}', {Rect(x1, y1, x2, y2)}, {(cityId is null ? "NULL" : cityId.ToString())})""");

    /// <param name="active">true, false or null (stored as NULL).</param>
    /// <param name="hasGeometry">false stores a NULL geometry.</param>
    public Task Point(int id, string? name, double x = 1, double y = 1, string? type = "Authorized",
        bool? active = true, bool hasGeometry = true, string? address = null, long? code = null,
        string? cityText = null, string? districtText = null) => Exec($"""
        INSERT INTO "Services"."ServicePoints" ("Id", "Code", "Name", "Type", "Address", "City", "District", "IsActive", "Geometry")
        VALUES ({id}, {Sql(code)}, {Sql(name)}, {Sql(type)}, {Sql(address)}, {Sql(cityText)}, {Sql(districtText)},
                {(active is null ? "NULL" : active.Value ? "true" : "false")},
                {(hasGeometry ? $"ST_SetSRID(ST_MakePoint({Num(x)}, {Num(y)}), 4326)" : "NULL")})
        """);

    /// <summary>Replaces the point's geometry with WKT in SRID 4326, or with NULL. Accepts values the column type allows but a map cannot show.</summary>
    public Task SetGeometry(int pointId, string? wkt) => Exec(
        $"""UPDATE "Services"."ServicePoints" SET "Geometry" = {(wkt is null ? "NULL" : $"ST_GeomFromText('{wkt}', 4326)")} WHERE "Id" = {pointId}""");

    public Task<int> Brand(string name) => Scalar<int>(
        $"""INSERT INTO "Brands"."Brands" ("Name") VALUES ({Sql(name)}) RETURNING "Id" """);

    public Task LinkBrand(int pointId, int brandId) => Exec(
        $"""INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId") VALUES ({pointId}, {brandId})""");

    public Task VehicleType(int pointId, string vehicleType) => Exec(
        $"""INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType") VALUES ({pointId}, '{vehicleType}')""");

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static string Sql(string? s) => s is null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string Sql(long? n) => n is null ? "NULL" : n.Value.ToString();
}
