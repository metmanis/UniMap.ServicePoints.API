using Microsoft.EntityFrameworkCore;
using UniMap.ServicePoints.API.Isochrones;

namespace UniMap.ServicePoints.API.Data;

/// <summary>The --migrate command: applies pending migrations and returns, with no web host.</summary>
public static class MigrationRunner
{
    public const string Flag = "--migrate";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        // The connection string is read from configuration (for example ConnectionStrings__ServicePoints) and never printed.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        var connectionString = configuration.GetConnectionString(IsochroneServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await error.WriteLineAsync(
                $"Connection string 'ConnectionStrings:{IsochroneServiceCollectionExtensions.ConnectionStringName}' is not configured.");
            return 1;
        }

        var options = new DbContextOptionsBuilder<ServicePointsDbContext>().UseNpgsql(connectionString).Options;
        await using var context = new ServicePointsDbContext(options);
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        await output.WriteLineAsync($"Applying {pending.Count} pending migration(s){(pending.Count > 0 ? ": " + string.Join(", ", pending) : "")}");
        await context.Database.MigrateAsync();
        await output.WriteLineAsync("Done.");
        return 0;
    }
}
