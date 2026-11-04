using Npgsql;
using UniMap.ServicePoints.API.Text;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Text;

public class TurkishNormalizerTests(ScratchDatabase db, CLocaleScratchDatabase cLocale)
    : IClassFixture<ScratchDatabase>, IClassFixture<CLocaleScratchDatabase>
{
    public static TheoryData<string?, string> Pairs => new()
    {
        { "Işık", "isik" },
        { "ISIK", "isik" },
        { "İstanbul", "istanbul" },
        { "İZMİR", "izmir" },
        { "ŞELL", "sell" },
        { "Güneş", "gunes" },
        { "ÇORUM", "corum" },
        { "ı", "i" },
        { "ÂÉ", "âé" },
        { "CAFÉ", "café" },
        { "Hâlâ", "hâlâ" },
        { null, "" },
        { "   ", "" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Normalize_maps_turkish_letters_and_lowercases(string? input, string expected)
        => Assert.Equal(expected, TurkishNormalizer.Normalize(input));

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Sql_normalize_text_matches_csharp(string? input, string expected)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Services".normalize_text(@p)""", c);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)input ?? DBNull.Value });
        Assert.Equal(expected, (string?)await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Sql_normalize_text_is_independent_of_database_locale(string? input, string expected)
    {
        await using var c = await cLocale.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Services".normalize_text(@p)""", c);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)input ?? DBNull.Value });
        Assert.Equal(expected, (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Brand_NormalizedName_is_generated_and_keeps_display_name()
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "Brands"."Brands" ("Name") VALUES ('Mağaza Çiğdem')
            RETURNING "Name" || '|' || "NormalizedName"
            """, c);
        Assert.Equal("Mağaza Çiğdem|magaza cigdem", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Brand_names_differing_only_by_case_or_turkish_letters_violate_unique_constraint()
    {
        await using var c = await db.OpenAsync();
        await using (var first = new NpgsqlCommand("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('Işık')""", c))
            await first.ExecuteNonQueryAsync();

        await using var dup = new NpgsqlCommand("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('ISIK')""", c);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }
}

/// <summary>Scratch database created with C collation/ctype, to prove normalization ignores the database locale.</summary>
public sealed class CLocaleScratchDatabase : ScratchDatabase
{
    protected override string CreateOptions => "TEMPLATE template0 LOCALE_PROVIDER libc ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'";
}
