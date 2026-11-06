using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261104120000_DistrictCity")]
public class DistrictCity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Each district goes to the city holding most of its area, because boundaries from different sources do not
        // line up exactly: a district can spill slightly into a neighbouring city, which rules out ST_Covers.
        // ST_Relate with '2********' keeps only cities whose interior shares an area with the district, so one that
        // merely touches along a border never qualifies. Nullable, for a district that lies in no city or has no boundary.
        // The location view does not depend on this column, and its trigger only fires for "Id" and "Geometry".
        migrationBuilder.Sql("""
            ALTER TABLE "Administrative"."Districts"
                ADD COLUMN "CityId" integer
                CONSTRAINT "FK_Districts_Cities_CityId" REFERENCES "Administrative"."Cities" ("Id") ON DELETE SET NULL;

            CREATE INDEX "IX_Districts_CityId" ON "Administrative"."Districts" ("CityId");

            UPDATE "Administrative"."Districts" d
            SET "CityId" = (
                SELECT c."Id"
                FROM "Administrative"."Cities" c
                WHERE ST_Relate(c."Geometry", d."Geometry", '2********')
                ORDER BY ST_Area(ST_Intersection(c."Geometry", d."Geometry")::geography) DESC, c."Id"
                LIMIT 1
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dropping the column also drops its index and foreign key.
        migrationBuilder.Sql("""ALTER TABLE "Administrative"."Districts" DROP COLUMN "CityId";""");
    }
}
