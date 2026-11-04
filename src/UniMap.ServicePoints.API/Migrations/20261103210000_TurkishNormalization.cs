using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261103210000_TurkishNormalization")]
public class TurkishNormalization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Mirrors TurkishNormalizer.Normalize. Every mapped character (including ASCII 'I') goes through
        // translate before lower. Remaining characters (Â, É, Ä ...) are lowercased with the builtin
        // pg_c_utf8 collation (Unicode simple case mapping, like ToLowerInvariant), so the result does not
        // depend on the database locale. COLLATE "C" would only lower ASCII.
        migrationBuilder.Sql("""
            CREATE FUNCTION "Services".normalize_text(input text) RETURNS text
            LANGUAGE sql IMMUTABLE PARALLEL SAFE
            AS $$
                SELECT CASE
                    WHEN input IS NULL OR input ~ '^\s*$' THEN ''
                    ELSE lower(translate(input, 'çÇğĞıIİöÖşŞüÜ', 'ccggiiioossuu') COLLATE "pg_c_utf8")
                END
            $$;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "Brands"."Brands"
                ADD COLUMN "NormalizedName" text GENERATED ALWAYS AS ("Services".normalize_text("Name")) STORED NOT NULL;
            ALTER TABLE "Brands"."Brands"
                ADD CONSTRAINT "UQ_Brands_NormalizedName" UNIQUE ("NormalizedName");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Brands"."Brands" DROP CONSTRAINT "UQ_Brands_NormalizedName";
            ALTER TABLE "Brands"."Brands" DROP COLUMN "NormalizedName";
            DROP FUNCTION "Services".normalize_text(text);
            """);
    }
}
