using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261104100000_ServicePointNameSearch")]
public class ServicePointNameSearch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Serves both the substring (LIKE) and the similarity search on the normalized name.
        migrationBuilder.Sql("""
            CREATE EXTENSION IF NOT EXISTS pg_trgm;

            CREATE INDEX "IX_ServicePoints_NormalizedName_Trgm"
                ON "Services"."ServicePoints" USING gin ("Services".normalize_text("Name") gin_trgm_ops);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The extension is left installed: other objects may depend on it.
        migrationBuilder.Sql("""DROP INDEX "Services"."IX_ServicePoints_NormalizedName_Trgm";""");
    }
}
