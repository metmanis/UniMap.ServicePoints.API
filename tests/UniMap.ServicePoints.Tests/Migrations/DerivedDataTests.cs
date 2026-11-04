using Npgsql;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

public class DerivedDataTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    // Tests in a class run one at a time on a shared database, so every test starts from empty tables.
    public async Task InitializeAsync()
    {
        await Exec("""
            DELETE FROM "Services"."ServicePoints";
            DELETE FROM "Brands"."Brands";
            DELETE FROM "Administrative"."Districts";
            DELETE FROM "Administrative"."Cities";
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task Exec(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Rect(double x1, double y1, double x2, double y2)
        => $"ST_Multi(ST_MakeEnvelope({x1}, {y1}, {x2}, {y2}, 4326))";

    private Task City(int id, double x1, double y1, double x2, double y2) => Exec(
        $"""INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry") VALUES ({id}, 'c{id}', {Rect(x1, y1, x2, y2)})""");

    private Task District(int id, double x1, double y1, double x2, double y2) => Exec(
        $"""INSERT INTO "Administrative"."Districts" ("Id", "Name", "Geometry") VALUES ({id}, 'd{id}', {Rect(x1, y1, x2, y2)})""");

    private Task Point(int id, double? x = null, double? y = null, string type = "Authorized") => Exec(
        $"""
        INSERT INTO "Services"."ServicePoints" ("Id", "Type", "Geometry")
        VALUES ({id}, '{type}', {(x is null ? "NULL" : $"ST_SetSRID(ST_MakePoint({x}, {y}), 4326)")})
        """);

    private Task Isochrone(int pointId, string vehicle) => Exec($"""
        INSERT INTO "Services"."ServicePointIsochrones" ("ServicePointId", "VehicleType", "RangeType", "RangeValue", "Geometry", "ComputedAt")
        VALUES ({pointId}, '{vehicle}', 'time', 600, {Rect(0, 0, 1, 1)}, now())
        """);

    private Task<long> IsochroneCount(int pointId, string? vehicle = null) => Scalar<long>($"""
        SELECT count(*) FROM "Services"."ServicePointIsochrones"
        WHERE "ServicePointId" = {pointId} {(vehicle is null ? "" : $"AND \"VehicleType\" = '{vehicle}'")}
        """);

    private async Task<string?> Location(int pointId) => await Scalar<string>($"""
        SELECT coalesce("CityId"::text, 'null') || '/' || coalesce("DistrictId"::text, 'null')
        FROM "Services"."ServicePointLocations" WHERE "ServicePointId" = {pointId}
        """);

    private Task<bool> IsStale(int pointId) => Scalar<bool>(
        $"""SELECT "IsStale" FROM "Services"."ServicePoints" WHERE "Id" = {pointId}""");

    private Task<int> Brand(string name) => Scalar<int>(
        $"""INSERT INTO "Brands"."Brands" ("Name") VALUES ('{name}') RETURNING "Id" """);

    [Fact]
    public async Task Point_inside_district_and_city_gets_both_ids()
    {
        await City(1, 0, 0, 10, 10);
        await District(11, 0, 0, 5, 10);
        await District(12, 5, 0, 10, 10);
        await Point(100, 2, 2);
        await Point(101, 8, 8);

        Assert.Equal("1/11", await Location(100));
        Assert.Equal("1/12", await Location(101));
    }

    [Fact]
    public async Task Point_outside_every_boundary_has_row_with_null_ids()
    {
        await City(1, 0, 0, 10, 10);
        await District(11, 0, 0, 5, 10);
        await Point(100, 50, 50);

        Assert.Equal("null/null", await Location(100));
    }

    [Fact]
    public async Task Point_with_null_geometry_has_row_with_null_ids()
    {
        await City(1, 0, 0, 10, 10);
        await Point(100);

        Assert.Equal("null/null", await Location(100));
    }

    [Fact]
    public async Task Point_in_district_but_in_no_city_takes_city_of_district_representative_point()
    {
        // The district sticks out of the city; the point is in the part outside the city.
        await City(1, 0, 0, 10, 10);
        await District(11, 2, 0, 14, 10);
        await Point(100, 12, 5);

        Assert.Equal("1/11", await Location(100));
    }

    [Fact]
    public async Task Point_on_district_boundary_takes_lowest_district_id()
    {
        await City(1, 0, 0, 10, 10);
        await District(7, 0, 0, 5, 10);
        await District(3, 5, 0, 10, 10);
        await Point(100, 5, 5);

        Assert.Equal("1/3", await Location(100));
        Assert.Equal(1, await Scalar<long>(
            """SELECT count(*) FROM "Services"."ServicePointLocations" WHERE "ServicePointId" = 100"""));
    }

    [Fact]
    public async Task Point_on_city_boundary_takes_lowest_city_id()
    {
        await City(9, 0, 0, 10, 10);
        await City(4, 10, 0, 20, 10);
        await Point(100, 10, 5);

        Assert.Equal("4/null", await Location(100));
    }

    [Fact]
    public async Task Inserting_a_point_adds_a_row_and_deleting_it_removes_the_row()
    {
        await City(1, 0, 0, 10, 10);
        await Point(100, 2, 2);
        Assert.Equal("1/null", await Location(100));

        await Exec("""DELETE FROM "Services"."ServicePoints" WHERE "Id" = 100""");
        Assert.Null(await Location(100));
    }

    [Fact]
    public async Task Changing_Type_sets_IsStale_and_keeps_isochrones()
    {
        await Point(100, 2, 2);
        await Isochrone(100, "car");

        await Exec("""UPDATE "Services"."ServicePoints" SET "Type" = 'Unauthorized' WHERE "Id" = 100""");

        Assert.True(await IsStale(100));
        Assert.Equal(1, await IsochroneCount(100));
    }

    [Fact]
    public async Task Inserting_or_deleting_a_brand_link_sets_IsStale_and_keeps_isochrones()
    {
        var brand = await Brand("b1");
        await Point(100, 2, 2);
        await Isochrone(100, "car");

        await Exec($"""INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId") VALUES (100, {brand})""");
        Assert.True(await IsStale(100));
        Assert.Equal(1, await IsochroneCount(100));

        await Exec("""UPDATE "Services"."ServicePoints" SET "IsStale" = false WHERE "Id" = 100""");
        Assert.False(await IsStale(100));

        await Exec("""DELETE FROM "Services"."ServicePointsBrands" WHERE "ServicePointId" = 100""");
        Assert.True(await IsStale(100));
        Assert.Equal(1, await IsochroneCount(100));
    }

    [Fact]
    public async Task Changing_the_brand_of_a_link_sets_IsStale()
    {
        var b1 = await Brand("b1");
        var b2 = await Brand("b2");
        await Point(100, 2, 2);
        await Exec($"""INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId") VALUES (100, {b1})""");
        await Exec("""UPDATE "Services"."ServicePoints" SET "IsStale" = false WHERE "Id" = 100""");

        await Exec($"""UPDATE "Services"."ServicePointsBrands" SET "BrandId" = {b2} WHERE "ServicePointId" = 100""");

        Assert.True(await IsStale(100));
    }

    [Fact]
    public async Task Changing_Geometry_deletes_isochrones_and_refreshes_the_view()
    {
        await City(1, 0, 0, 10, 10);
        await Point(100, 50, 50);
        await Isochrone(100, "car");
        await Isochrone(100, "truck");
        Assert.Equal("null/null", await Location(100));

        await Exec("""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(2, 2), 4326) WHERE "Id" = 100""");

        Assert.Equal(0, await IsochroneCount(100));
        Assert.Equal("1/null", await Location(100));
    }

    [Fact]
    public async Task Bulk_geometry_update_clears_every_changed_point_and_leaves_others()
    {
        await Point(100, 1, 1);
        await Point(101, 2, 2);
        await Point(102, 3, 3);
        await Isochrone(100, "car");
        await Isochrone(101, "car");
        await Isochrone(102, "car");

        await Exec("""
            UPDATE "Services"."ServicePoints"
            SET "Geometry" = ST_SetSRID(ST_MakePoint(ST_X("Geometry") + 1, 1), 4326)
            WHERE "Id" IN (100, 101)
            """);

        Assert.Equal(0, await IsochroneCount(100));
        Assert.Equal(0, await IsochroneCount(101));
        Assert.Equal(1, await IsochroneCount(102));
    }

    [Fact]
    public async Task Update_that_changes_nothing_sets_no_flag_deletes_nothing_and_does_not_refresh()
    {
        await Point(100, 2, 2);
        await Isochrone(100, "car");

        // Add a city while the triggers are off, so the view is out of date. A refresh would pick it up.
        await Exec("""ALTER TABLE "Administrative"."Cities" DISABLE TRIGGER USER""");
        await City(1, 0, 0, 10, 10);
        await Exec("""ALTER TABLE "Administrative"."Cities" ENABLE TRIGGER USER""");
        Assert.Equal("null/null", await Location(100));

        await Exec("""UPDATE "Services"."ServicePoints" SET "Type" = "Type", "Geometry" = "Geometry" WHERE "Id" = 100""");

        Assert.False(await IsStale(100));
        Assert.Equal(1, await IsochroneCount(100));
        Assert.Equal("null/null", await Location(100));

        // A real geometry change does refresh, and then shows the city.
        await Exec("""UPDATE "Services"."ServicePoints" SET "Geometry" = ST_SetSRID(ST_MakePoint(3, 3), 4326) WHERE "Id" = 100""");
        Assert.Equal("1/null", await Location(100));
    }

    [Fact]
    public async Task Inserting_a_vehicle_type_deletes_only_that_vehicles_isochrones()
    {
        await Point(100, 2, 2);
        await Isochrone(100, "car");
        await Isochrone(100, "truck");

        await Exec("""INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType") VALUES (100, 'HeavyDuty')""");

        Assert.Equal(0, await IsochroneCount(100, "truck"));
        Assert.Equal(1, await IsochroneCount(100, "car"));
    }

    [Fact]
    public async Task Deleting_a_vehicle_type_deletes_only_that_vehicles_isochrones()
    {
        await Point(100, 2, 2);
        await Exec("""
            INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType")
            VALUES (100, 'PassengerCar'), (100, 'HeavyDuty')
            """);
        await Isochrone(100, "car");
        await Isochrone(100, "truck");

        await Exec("""DELETE FROM "Services"."ServicePointVehicleTypes" WHERE "ServicePointId" = 100 AND "VehicleType" = 'PassengerCar'""");

        Assert.Equal(0, await IsochroneCount(100, "car"));
        Assert.Equal(1, await IsochroneCount(100, "truck"));
    }

    [Fact]
    public async Task Updating_a_vehicle_type_deletes_isochrones_of_both_old_and_new_vehicle()
    {
        await Point(100, 2, 2);
        await Exec("""INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType") VALUES (100, 'PassengerCar')""");
        await Isochrone(100, "car");
        await Isochrone(100, "truck");

        await Exec("""UPDATE "Services"."ServicePointVehicleTypes" SET "VehicleType" = 'HeavyDuty' WHERE "ServicePointId" = 100""");

        Assert.Equal(0, await IsochroneCount(100));
    }

    [Fact]
    public async Task Deleting_a_point_cascades_to_brand_and_vehicle_links_without_error()
    {
        var brand = await Brand("b1");
        await Point(100, 2, 2);
        await Exec($"""INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId") VALUES (100, {brand})""");
        await Exec("""INSERT INTO "Services"."ServicePointVehicleTypes" ("ServicePointId", "VehicleType") VALUES (100, 'HeavyDuty')""");
        await Isochrone(100, "truck");

        await Exec("""DELETE FROM "Services"."ServicePoints" WHERE "Id" = 100""");

        Assert.Equal(0, await Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointsBrands" """));
        Assert.Equal(0, await IsochroneCount(100));
        Assert.Null(await Location(100));
    }

    [Fact]
    public async Task Changing_boundaries_refreshes_the_view()
    {
        await Point(100, 2, 2);
        Assert.Equal("null/null", await Location(100));

        await City(1, 0, 0, 10, 10);
        Assert.Equal("1/null", await Location(100));

        await District(11, 0, 0, 5, 5);
        Assert.Equal("1/11", await Location(100));

        await Exec($"""UPDATE "Administrative"."Districts" SET "Geometry" = {Rect(20, 20, 30, 30)} WHERE "Id" = 11""");
        Assert.Equal("1/null", await Location(100));

        await Exec($"""UPDATE "Administrative"."Cities" SET "Geometry" = {Rect(20, 20, 30, 30)} WHERE "Id" = 1""");
        Assert.Equal("null/null", await Location(100));

        await Exec("""DELETE FROM "Administrative"."Districts" """);
        await Exec("""DELETE FROM "Administrative"."Cities" """);
        Assert.Equal("null/null", await Location(100));
    }

    [Fact]
    public async Task Refresh_function_exists_and_runs()
    {
        await Point(100, 2, 2);
        await Exec("""SELECT "Services".refresh_service_point_locations()""");
        Assert.Equal("null/null", await Location(100));
    }
}
