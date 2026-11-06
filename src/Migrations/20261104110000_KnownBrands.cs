using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using UniMap.ServicePoints.API.Data;

#nullable disable

namespace UniMap.ServicePoints.API.Migrations;

[DbContext(typeof(ServicePointsDbContext))]
[Migration("20261104110000_KnownBrands")]
public class KnownBrands : Migration
{
    // The owner adjusts this list by hand.
    private static readonly string[] Names =
    [
        "Alfa Romeo", "Anadolu Isuzu", "Audi", "BMC", "BMW", "BYD", "Chery", "Chevrolet", "Citroen", "Cupra", "Dacia", "DAF",
        "DS Automobiles", "Fiat", "Ford", "Honda", "Hyundai", "Iveco", "Jaguar", "Jeep", "Karsan", "Kia", "Land Rover", "Lexus",
        "MAN", "Mazda", "Mercedes-Benz", "MG", "Mini", "Mitsubishi", "Nissan", "Opel", "Otokar", "Peugeot", "Porsche", "Renault",
        "Scania", "Seat", "Skoda", "Smart", "Subaru", "Suzuki", "Temsa", "Tesla", "Togg", "Toyota", "Volkswagen", "Volvo",
    ];

    // Migration SQL cannot take parameters. The names are constants of this file, never input, and quotes are doubled anyway.
    private static IEnumerable<string> Literals() => Names.Select(n => $"'{n.Replace("'", "''")}'");

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A name that differs from an existing one only by case or Turkish letters is already there, so it is skipped.
        var values = string.Join(", ", Literals().Select(l => $"({l})"));
        migrationBuilder.Sql($"""INSERT INTO "Brands"."Brands" ("Name") VALUES {values} ON CONFLICT ("NormalizedName") DO NOTHING""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Exact names only: a brand added by hand under another spelling stays. Links of a removed brand go with it.
        migrationBuilder.Sql($"""DELETE FROM "Brands"."Brands" WHERE "Name" IN ({string.Join(", ", Literals())})""");
    }
}
