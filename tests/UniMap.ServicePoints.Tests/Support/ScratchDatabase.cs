using Microsoft.EntityFrameworkCore;
using Npgsql;
using UniMap.ServicePoints.API.Data;

namespace UniMap.ServicePoints.Tests.Support;

/// <summary>
/// Creates a throwaway PostGIS database (name must start with <see cref="Prefix"/>), applies the baseline
/// schema and the EF migrations, and drops it on dispose. Never touches any other database.
/// Server settings come from PG_HOST, PG_PORT, PG_USER and PG_PASSWORD; PG_DATABASE is never used.
/// </summary>
public class ScratchDatabase : IAsyncLifetime
{
    public const string Prefix = "unimap_sp_test_";

    /// <summary>Extra CREATE DATABASE options, e.g. a non-default locale.</summary>
    protected virtual string CreateOptions => "";

    /// <summary>False leaves the database at the baseline schema, for tests that apply the migrations themselves.</summary>
    protected virtual bool ApplyMigrations => true;

    public string Name { get; } = Prefix + Guid.NewGuid().ToString("N")[..12];

    private static NpgsqlConnectionStringBuilder Server() => new()
    {
        Host = Environment.GetEnvironmentVariable("PG_HOST") ?? "localhost",
        Port = int.Parse(Environment.GetEnvironmentVariable("PG_PORT") ?? "5432"),
        Username = Environment.GetEnvironmentVariable("PG_USER"),
        Password = Environment.GetEnvironmentVariable("PG_PASSWORD"),
        Database = "postgres",
    };

    public string ConnectionString
    {
        get
        {
            Guard(Name);
            var b = Server();
            b.Database = Name;
            return b.ConnectionString;
        }
    }

    private static void Guard(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to use database '{name}': name must start with '{Prefix}'.");
    }

    public async ValueTask<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public async Task InitializeAsync()
    {
        Guard(Name);
        await using (var admin = new NpgsqlConnection(Server().ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{Name}\" {CreateOptions}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using (var conn = await OpenAsync())
        {
            var baseline = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Fixtures", "baseline.sql"));
            await using var cmd = new NpgsqlCommand(baseline, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        if (!ApplyMigrations) return;

        var options = new DbContextOptionsBuilder<ServicePointsDbContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new ServicePointsDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Guard(Name);
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Server().ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
