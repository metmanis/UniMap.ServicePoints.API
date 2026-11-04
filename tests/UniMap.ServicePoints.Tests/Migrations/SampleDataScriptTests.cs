using Npgsql;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

/// <summary>Runs scripts/seed-sample-service-point-data.sql as shipped. It is development data, not a migration.</summary>
public class SampleDataScriptTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private static readonly string[] HeavyBrands =
        ["BMC", "DAF", "Iveco", "MAN", "Mercedes-Benz", "Scania", "Volvo", "Anadolu Isuzu", "Karsan", "Otokar", "Temsa"];

    private readonly ServicePointSeed _seed = new(db);

    // Points 1 to 30, including inactive ones and ones without a location: the script covers every point.
    public async Task InitializeAsync()
    {
        await _seed.Exec("""DELETE FROM "Services"."ServicePoints" """);
        for (var id = 1; id <= 30; id++)
            await _seed.Point(id, $"P{id}", active: id % 7 != 0, hasGeometry: id % 11 != 0, type: "Authorized");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Script => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "seed-sample-service-point-data.sql"));

    private Task RunScript() => _seed.Exec(Script);

    private Task<string?> Rows(string sql) => _seed.Scalar<string>(sql);

    private const string VehicleRows = """
        SELECT string_agg("ServicePointId" || ':' || "VehicleType", ',' ORDER BY "ServicePointId", "VehicleType")
        FROM "Services"."ServicePointVehicleTypes"
        """;

    private const string BrandRows = """
        SELECT string_agg(l."ServicePointId" || ':' || b."Name", ',' ORDER BY l."ServicePointId", b."Name")
        FROM "Services"."ServicePointsBrands" l JOIN "Brands"."Brands" b ON b."Id" = l."BrandId"
        """;

    [Fact]
    public async Task Every_point_gets_a_passenger_car_and_one_in_five_also_gets_heavy_duty()
    {
        await RunScript();

        Assert.Equal(30, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointVehicleTypes" WHERE "VehicleType" = 'PassengerCar'"""));
        Assert.Equal("5,10,15,20,25,30", await Rows("""
            SELECT string_agg("ServicePointId"::text, ',' ORDER BY "ServicePointId")
            FROM "Services"."ServicePointVehicleTypes" WHERE "VehicleType" = 'HeavyDuty'
            """));
    }

    [Fact]
    public async Task Every_point_gets_one_or_two_brands_and_heavy_duty_points_get_a_heavy_duty_brand()
    {
        await RunScript();

        Assert.Equal(0, await _seed.Scalar<long>("""
            SELECT count(*) FROM "Services"."ServicePoints" sp
            WHERE (SELECT count(*) FROM "Services"."ServicePointsBrands" l WHERE l."ServicePointId" = sp."Id") NOT IN (1, 2)
            """));
        // 30 first brands and a second brand for the ten points whose Id is divisible by 3.
        Assert.Equal(40, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointsBrands" """));

        var heavy = string.Join(",", HeavyBrands.Select(b => "'" + b + "'"));
        Assert.Equal(0, await _seed.Scalar<long>($"""
            SELECT count(*) FROM "Services"."ServicePointVehicleTypes" vt
            WHERE vt."VehicleType" = 'HeavyDuty' AND NOT EXISTS (
                SELECT 1 FROM "Services"."ServicePointsBrands" l JOIN "Brands"."Brands" b ON b."Id" = l."BrandId"
                WHERE l."ServicePointId" = vt."ServicePointId" AND b."Name" IN ({heavy}))
            """));
        Assert.Equal(0, await _seed.Scalar<long>($"""
            SELECT count(*) FROM "Services"."ServicePointVehicleTypes" vt
            JOIN "Services"."ServicePointsBrands" l ON l."ServicePointId" = vt."ServicePointId"
            JOIN "Brands"."Brands" b ON b."Id" = l."BrandId"
            WHERE vt."VehicleType" = 'PassengerCar' AND vt."ServicePointId" % 5 <> 0 AND b."Name" IN ({heavy})
            """));
    }

    [Fact]
    public async Task No_point_is_left_stale_and_nothing_else_about_the_points_or_brands_changes()
    {
        var before = await Rows("""SELECT md5(string_agg(sp::text, '|' ORDER BY "Id")) FROM "Services"."ServicePoints" sp""");
        var brandsBefore = await Rows("""SELECT md5(string_agg(b::text, '|' ORDER BY "Id")) FROM "Brands"."Brands" b""");

        await RunScript();

        Assert.Equal(0, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePoints" WHERE "IsStale" """));
        Assert.Equal(before, await Rows("""SELECT md5(string_agg(sp::text, '|' ORDER BY "Id")) FROM "Services"."ServicePoints" sp"""));
        Assert.Equal(brandsBefore, await Rows("""SELECT md5(string_agg(b::text, '|' ORDER BY "Id")) FROM "Brands"."Brands" b"""));
    }

    [Fact]
    public async Task A_point_that_was_already_stale_for_a_real_reason_stays_stale()
    {
        await _seed.Exec("""UPDATE "Services"."ServicePoints" SET "IsStale" = true WHERE "Id" = 4""");

        await RunScript();

        Assert.Equal("4", await Rows("""SELECT string_agg("Id"::text, ',') FROM "Services"."ServicePoints" WHERE "IsStale" """));
    }

    [Fact]
    public async Task Running_it_twice_gives_the_same_rows()
    {
        await RunScript();
        var vehicles = await Rows(VehicleRows);
        var brands = await Rows(BrandRows);

        await RunScript();

        Assert.Equal(vehicles, await Rows(VehicleRows));
        Assert.Equal(brands, await Rows(BrandRows));
        Assert.Equal(0, await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePoints" WHERE "IsStale" """));
    }

    [Fact]
    public async Task Editing_the_brand_list_and_running_again_keeps_existing_links_and_never_exceeds_two_brands()
    {
        await RunScript();
        var vehicles = await Rows(VehicleRows);
        var linksBefore = await Rows(BrandRows);

        // Adding a name that sorts first and removing one both shift every position in a pool.
        await _seed.Exec("""
            INSERT INTO "Brands"."Brands" ("Name") VALUES ('Aaa Added Brand'), ('Zzz Added Heavy');
            DELETE FROM "Brands"."Brands" WHERE "Name" = 'Fiat';
            """);
        var linksAfterEdit = await Rows(BrandRows);
        await RunScript();

        Assert.Equal(vehicles, await Rows(VehicleRows));
        // Whatever the edit itself removed, the script adds nothing next to the surviving links.
        Assert.Equal(linksAfterEdit, await Rows(BrandRows));
        Assert.Equal(0, await _seed.Scalar<long>("""
            SELECT count(*) FROM (SELECT "ServicePointId" FROM "Services"."ServicePointsBrands"
                                  GROUP BY "ServicePointId" HAVING count(*) > 2) t
            """));
        Assert.NotEqual(linksBefore, linksAfterEdit);
    }

    [Fact]
    public async Task A_point_that_lost_all_its_brands_is_seeded_again_with_one_or_two_and_others_are_untouched()
    {
        await RunScript();
        await _seed.Exec("""DELETE FROM "Services"."ServicePointsBrands" WHERE "ServicePointId" = 8""");
        var others = await Rows(BrandRows + """ WHERE l."ServicePointId" <> 8""");

        await RunScript();

        Assert.InRange(await _seed.Scalar<long>("""SELECT count(*) FROM "Services"."ServicePointsBrands" WHERE "ServicePointId" = 8"""), 1, 2);
        Assert.Equal(others, await Rows(BrandRows + """ WHERE l."ServicePointId" <> 8"""));
    }

    [Fact]
    public async Task Links_and_vehicle_types_adjusted_by_hand_survive_a_rerun()
    {
        await RunScript();
        await _seed.Exec("""
            INSERT INTO "Brands"."Brands" ("Name") VALUES ('Hand Picked');
            DELETE FROM "Services"."ServicePointsBrands" WHERE "ServicePointId" = 4;
            INSERT INTO "Services"."ServicePointsBrands" ("ServicePointId", "BrandId")
                SELECT 4, "Id" FROM "Brands"."Brands" WHERE "Name" = 'Hand Picked';
            DELETE FROM "Services"."ServicePointVehicleTypes" WHERE "ServicePointId" = 5 AND "VehicleType" = 'PassengerCar';
            """);
        var brandsBefore = await Rows(BrandRows);
        var vehiclesBefore = await Rows(VehicleRows);

        await RunScript();

        Assert.Equal("4:Hand Picked", await Rows(BrandRows + """ WHERE l."ServicePointId" = 4"""));
        Assert.Equal("5:HeavyDuty", await Rows(VehicleRows + """ WHERE "ServicePointId" = 5"""));
        Assert.Equal(brandsBefore, await Rows(BrandRows));
        Assert.Equal(vehiclesBefore, await Rows(VehicleRows));
    }

    [Fact]
    public void The_script_says_it_is_sample_data_and_uses_one_transaction()
    {
        var script = Script;

        Assert.Contains("SAMPLE DATA FOR DEVELOPMENT ONLY", script);
        Assert.Contains("BEGIN;", script);
        Assert.Contains("COMMIT;", script);
    }
}
