using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using UniMap.ServicePoints.API.Data;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

public class DistrictCityTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>
{
    private const string PreviousMigration = "20261104110000_KnownBrands";

    private async Task Exec(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int?> CityOf(int districtId)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""SELECT "CityId" FROM "Administrative"."Districts" WHERE "Id" = {districtId}""", c);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : (int?)value;
    }

    private async Task<bool> ColumnExists()
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'Administrative' AND table_name = 'Districts' AND column_name = 'CityId')
            """, c);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task MigrateTo(string? target)
    {
        var options = new DbContextOptionsBuilder<ServicePointsDbContext>().UseNpgsql(db.ConnectionString).Options;
        await using var context = new ServicePointsDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static string Rect(int x1, int y1, int x2, int y2) => $"ST_Multi(ST_MakeEnvelope({x1}, {y1}, {x2}, {y2}, 4326))";

    [Fact]
    public async Task The_migration_assigns_each_district_to_the_city_holding_most_of_it_and_down_removes_the_column()
    {
        await MigrateTo(PreviousMigration);
        await Exec($"""
            INSERT INTO "Administrative"."Cities" ("Id", "Name", "Geometry") VALUES
                (1, 'West', {Rect(0, 0, 10, 10)}),
                (2, 'East', {Rect(10, 0, 20, 10)});
            INSERT INTO "Administrative"."Districts" ("Id", "Name", "Geometry") VALUES
                (10, 'Inside west', {Rect(1, 1, 2, 2)}),
                (11, 'Mostly east', {Rect(9, 1, 12, 2)}),
                (12, 'On the west side of the border', {Rect(9, 3, 10, 4)}),
                (13, 'Outside every city', {Rect(50, 50, 51, 51)}),
                (14, 'Only touches west', {Rect(-1, 5, 0, 6)}),
                (15, 'No boundary', NULL);
            """);

        await MigrateTo(null);

        Assert.Equal(1, await CityOf(10));
        Assert.Equal(2, await CityOf(11));
        Assert.Equal(1, await CityOf(12));
        Assert.Null(await CityOf(13));
        Assert.Null(await CityOf(14));
        Assert.Null(await CityOf(15));

        await Exec("""DELETE FROM "Administrative"."Cities" WHERE "Id" = 1""");
        Assert.Null(await CityOf(10));

        await MigrateTo(PreviousMigration);
        Assert.False(await ColumnExists());

        await MigrateTo(null);
        Assert.True(await ColumnExists());
    }
}
