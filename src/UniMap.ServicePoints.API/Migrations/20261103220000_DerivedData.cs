using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261103220000_DerivedData")]
public class DerivedData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // One row per service point (null ids when nothing matches or the point has no geometry).
        // ST_Covers instead of ST_Within so a point exactly on a boundary still matches; ST_Within is false there.
        // Ties on a shared boundary go to the lowest Id. A point in a district but in no city takes the city
        // that contains the district's representative point.
        migrationBuilder.Sql("""
            CREATE MATERIALIZED VIEW "Services"."ServicePointLocations" AS
            SELECT sp."Id" AS "ServicePointId",
                   COALESCE(direct_city."Id", district_city."Id") AS "CityId",
                   district."Id" AS "DistrictId"
            FROM "Services"."ServicePoints" sp
            LEFT JOIN LATERAL (
                SELECT d."Id", d."Geometry"
                FROM "Administrative"."Districts" d
                WHERE ST_Covers(d."Geometry", sp."Geometry")
                ORDER BY d."Id" LIMIT 1
            ) district ON true
            LEFT JOIN LATERAL (
                SELECT c."Id"
                FROM "Administrative"."Cities" c
                WHERE ST_Covers(c."Geometry", sp."Geometry")
                ORDER BY c."Id" LIMIT 1
            ) direct_city ON true
            LEFT JOIN LATERAL (
                SELECT c."Id"
                FROM "Administrative"."Cities" c
                WHERE direct_city."Id" IS NULL
                  AND district."Id" IS NOT NULL
                  AND ST_Covers(c."Geometry", ST_PointOnSurface(district."Geometry"))
                ORDER BY c."Id" LIMIT 1
            ) district_city ON true;

            CREATE UNIQUE INDEX "UX_ServicePointLocations_ServicePointId"
                ON "Services"."ServicePointLocations" ("ServicePointId");

            -- The single place that decides how the view is refreshed.
            CREATE FUNCTION "Services".refresh_service_point_locations() RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                REFRESH MATERIALIZED VIEW CONCURRENTLY "Services"."ServicePointLocations";
            END
            $$;

            CREATE FUNCTION "Services".trg_refresh_service_point_locations() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM "Services".refresh_service_point_locations();
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER trg_service_points_refresh_locations
                AFTER INSERT OR DELETE ON "Services"."ServicePoints"
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_refresh_service_point_locations();

            CREATE TRIGGER trg_cities_refresh_locations
                AFTER INSERT OR DELETE OR UPDATE OF "Id", "Geometry" ON "Administrative"."Cities"
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_refresh_service_point_locations();

            CREATE TRIGGER trg_districts_refresh_locations
                AFTER INSERT OR DELETE OR UPDATE OF "Id", "Geometry" ON "Administrative"."Districts"
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_refresh_service_point_locations();
            """);

        // Type change marks the point stale (row trigger on the changed row, so no second UPDATE is needed).
        migrationBuilder.Sql("""
            CREATE FUNCTION "Services".trg_service_points_type_stale() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                NEW."IsStale" := true;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER trg_service_points_type_stale
                BEFORE UPDATE ON "Services"."ServicePoints"
                FOR EACH ROW WHEN (OLD."Type" IS DISTINCT FROM NEW."Type")
                EXECUTE FUNCTION "Services".trg_service_points_type_stale();
            """);

        // Geometry change deletes the point's cached isochrones and refreshes the view once per statement.
        // Transition tables cannot be combined with a column list, so the changed rows are found by comparison.
        migrationBuilder.Sql("""
            CREATE FUNCTION "Services".trg_service_points_geometry_changed() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                moved integer[];
            BEGIN
                SELECT array_agg(n."Id") INTO moved
                FROM new_rows n
                JOIN old_rows o ON o."Id" = n."Id"
                WHERE o."Geometry" IS DISTINCT FROM n."Geometry";

                IF moved IS NOT NULL THEN
                    DELETE FROM "Services"."ServicePointIsochrones" WHERE "ServicePointId" = ANY (moved);
                    PERFORM "Services".refresh_service_point_locations();
                END IF;
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER trg_service_points_geometry_changed
                AFTER UPDATE ON "Services"."ServicePoints"
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_service_points_geometry_changed();
            """);

        // Brand link insert, delete or change marks the point stale. When a point is deleted its links go with
        // it, the point row is already gone, and the UPDATE simply matches nothing.
        migrationBuilder.Sql("""
            CREATE FUNCTION "Services".trg_brand_links_stale() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP IN ('INSERT', 'UPDATE') THEN
                    UPDATE "Services"."ServicePoints" SET "IsStale" = true
                    WHERE "Id" IN (SELECT "ServicePointId" FROM new_rows);
                END IF;
                IF TG_OP IN ('DELETE', 'UPDATE') THEN
                    UPDATE "Services"."ServicePoints" SET "IsStale" = true
                    WHERE "Id" IN (SELECT "ServicePointId" FROM old_rows);
                END IF;
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER trg_brand_links_insert_stale
                AFTER INSERT ON "Services"."ServicePointsBrands"
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_brand_links_stale();

            CREATE TRIGGER trg_brand_links_delete_stale
                AFTER DELETE ON "Services"."ServicePointsBrands"
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_brand_links_stale();

            CREATE TRIGGER trg_brand_links_update_stale
                AFTER UPDATE ON "Services"."ServicePointsBrands"
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_brand_links_stale();
            """);

        // Vehicle type insert, delete or change deletes that vehicle's cached isochrones.
        // Stored values differ: PassengerCar -> car, HeavyDuty -> truck.
        migrationBuilder.Sql("""
            CREATE FUNCTION "Services".trg_vehicle_types_invalidate() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP IN ('INSERT', 'UPDATE') THEN
                    DELETE FROM "Services"."ServicePointIsochrones" i
                    USING new_rows r
                    WHERE i."ServicePointId" = r."ServicePointId"
                      AND i."VehicleType" = CASE r."VehicleType" WHEN 'PassengerCar' THEN 'car' WHEN 'HeavyDuty' THEN 'truck' END;
                END IF;
                IF TG_OP IN ('DELETE', 'UPDATE') THEN
                    DELETE FROM "Services"."ServicePointIsochrones" i
                    USING old_rows r
                    WHERE i."ServicePointId" = r."ServicePointId"
                      AND i."VehicleType" = CASE r."VehicleType" WHEN 'PassengerCar' THEN 'car' WHEN 'HeavyDuty' THEN 'truck' END;
                END IF;
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER trg_vehicle_types_insert_invalidate
                AFTER INSERT ON "Services"."ServicePointVehicleTypes"
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_vehicle_types_invalidate();

            CREATE TRIGGER trg_vehicle_types_delete_invalidate
                AFTER DELETE ON "Services"."ServicePointVehicleTypes"
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_vehicle_types_invalidate();

            CREATE TRIGGER trg_vehicle_types_update_invalidate
                AFTER UPDATE ON "Services"."ServicePointVehicleTypes"
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION "Services".trg_vehicle_types_invalidate();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER trg_vehicle_types_update_invalidate ON "Services"."ServicePointVehicleTypes";
            DROP TRIGGER trg_vehicle_types_delete_invalidate ON "Services"."ServicePointVehicleTypes";
            DROP TRIGGER trg_vehicle_types_insert_invalidate ON "Services"."ServicePointVehicleTypes";
            DROP FUNCTION "Services".trg_vehicle_types_invalidate();

            DROP TRIGGER trg_brand_links_update_stale ON "Services"."ServicePointsBrands";
            DROP TRIGGER trg_brand_links_delete_stale ON "Services"."ServicePointsBrands";
            DROP TRIGGER trg_brand_links_insert_stale ON "Services"."ServicePointsBrands";
            DROP FUNCTION "Services".trg_brand_links_stale();

            DROP TRIGGER trg_service_points_geometry_changed ON "Services"."ServicePoints";
            DROP FUNCTION "Services".trg_service_points_geometry_changed();

            DROP TRIGGER trg_service_points_type_stale ON "Services"."ServicePoints";
            DROP FUNCTION "Services".trg_service_points_type_stale();

            DROP TRIGGER trg_districts_refresh_locations ON "Administrative"."Districts";
            DROP TRIGGER trg_cities_refresh_locations ON "Administrative"."Cities";
            DROP TRIGGER trg_service_points_refresh_locations ON "Services"."ServicePoints";
            DROP FUNCTION "Services".trg_refresh_service_point_locations();
            DROP FUNCTION "Services".refresh_service_point_locations();
            DROP MATERIALIZED VIEW "Services"."ServicePointLocations";
            """);
    }
}
