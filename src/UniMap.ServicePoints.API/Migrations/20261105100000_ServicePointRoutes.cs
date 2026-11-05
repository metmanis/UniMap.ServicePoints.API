using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261105100000_ServicePointRoutes")]
public class ServicePointRoutes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // fuzzystrmatch provides levenshtein(), used by the search ranking.
        migrationBuilder.Sql("""
            CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;

            CREATE TABLE "Services"."ServicePointRoutes" (
                "FromServicePointId" integer NOT NULL,
                "ToServicePointId" integer NOT NULL,
                "FromX" double precision NOT NULL,
                "FromY" double precision NOT NULL,
                "ToX" double precision NOT NULL,
                "ToY" double precision NOT NULL,
                "Geometry" geometry(LineString, 4326) NOT NULL,
                "DistanceMeters" double precision NOT NULL,
                "DurationSeconds" integer NOT NULL,
                "CalculatedAt" timestamptz NOT NULL,
                CONSTRAINT "PK_ServicePointRoutes" PRIMARY KEY ("FromServicePointId", "ToServicePointId"),
                CONSTRAINT "FK_ServicePointRoutes_From" FOREIGN KEY ("FromServicePointId")
                    REFERENCES "Services"."ServicePoints" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ServicePointRoutes_To" FOREIGN KEY ("ToServicePointId")
                    REFERENCES "Services"."ServicePoints" ("Id") ON DELETE CASCADE,
                CONSTRAINT "CK_ServicePointRoutes_DifferentPoints" CHECK ("FromServicePointId" <> "ToServicePointId")
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The extension is left installed: other objects may depend on it.
        migrationBuilder.Sql("""DROP TABLE "Services"."ServicePointRoutes";""");
    }
}
