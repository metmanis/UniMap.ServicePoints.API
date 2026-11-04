using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using UniMap.ServicePoints.API.Data;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

public class KnownBrandsTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>
{
    private const string PreviousMigration = "20261104100000_ServicePointNameSearch";

    private static readonly string[] Known =
    [
        "Alfa Romeo", "Anadolu Isuzu", "Audi", "BMC", "BMW", "BYD", "Chery", "Chevrolet", "Citroen", "Cupra", "Dacia", "DAF",
        "DS Automobiles", "Fiat", "Ford", "Honda", "Hyundai", "Iveco", "Jaguar", "Jeep", "Karsan", "Kia", "Land Rover", "Lexus",
        "MAN", "Mazda", "Mercedes-Benz", "MG", "Mini", "Mitsubishi", "Nissan", "Opel", "Otokar", "Peugeot", "Porsche", "Renault",
        "Scania", "Seat", "Skoda", "Smart", "Subaru", "Suzuki", "Temsa", "Tesla", "Togg", "Toyota", "Volkswagen", "Volvo",
    ];

    private async Task<string[]> BrandNames()
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Name" FROM "Brands"."Brands" ORDER BY "Name" """, c);
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task Exec(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task MigrateTo(string? target)
    {
        var options = new DbContextOptionsBuilder<ServicePointsDbContext>().UseNpgsql(db.ConnectionString).Options;
        await using var context = new ServicePointsDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    [Fact]
    public async Task The_migration_inserts_the_known_brands()
    {
        Assert.Equal(Known.Order(StringComparer.Ordinal), await BrandNames());
    }

    [Fact]
    public async Task Down_removes_only_the_known_brands_and_up_again_neither_fails_nor_duplicates()
    {
        await Exec("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('Zzz Own Brand')""");

        await MigrateTo(PreviousMigration);
        Assert.Equal(["Zzz Own Brand"], await BrandNames());

        // A name differing only by case and Turkish letters is already there, so the insert skips it.
        await Exec("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('AUDI')""");
        await MigrateTo(null);

        var names = await BrandNames();
        Assert.Equal(Known.Where(n => n != "Audi").Append("AUDI").Append("Zzz Own Brand").Order(StringComparer.Ordinal), names);

        await MigrateTo(PreviousMigration);
        Assert.Equal(["AUDI", "Zzz Own Brand"], await BrandNames());
        await MigrateTo(null);
    }
}
