using System.Diagnostics;
using Npgsql;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

/// <summary>A scratch database at the baseline schema only, so the --migrate command has work to do.</summary>
public sealed class BaselineOnlyScratchDatabase : ScratchDatabase
{
    protected override bool ApplyMigrations => false;
}

/// <summary>Starts the real application as a process with --migrate. The connection string travels in the environment.</summary>
public class MigrateCommandTests(BaselineOnlyScratchDatabase db) : IClassFixture<BaselineOnlyScratchDatabase>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static async Task<(int ExitCode, string Output)> Run(string? connectionString)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "UniMap.ServicePoints.API.dll"));
        info.ArgumentList.Add("--migrate");
        info.Environment.Remove("ConnectionStrings__ServicePoints");
        if (connectionString is not null) info.Environment["ConnectionStrings__ServicePoints"] = connectionString;

        using var process = Process.Start(info)!;
        var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        using var timeout = new CancellationTokenSource(Limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException($"--migrate did not exit within {Limit.TotalSeconds} seconds: it must not start a web host.");
        }
        return (process.ExitCode, string.Concat(await output));
    }

    private async Task<long> Count(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Migrate_applies_the_pending_migrations_exits_and_can_run_again()
    {
        Assert.Equal(0, await Count("""SELECT count(*) FROM pg_namespace WHERE nspname = 'Brands'"""));

        var first = await Run(db.ConnectionString);

        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Equal(1, await Count("""SELECT count(*) FROM pg_namespace WHERE nspname = 'Brands'"""));
        Assert.Equal(48, await Count("""SELECT count(*) FROM "Brands"."Brands" """));
        Assert.Equal(7, await Count("""SELECT count(*) FROM "__EFMigrationsHistory" """));

        var second = await Run(db.ConnectionString);

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(7, await Count("""SELECT count(*) FROM "__EFMigrationsHistory" """));
    }

    [Fact]
    public async Task Migrate_without_a_connection_string_fails_naming_the_key()
    {
        var result = await Run(null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ConnectionStrings:ServicePoints", result.Output);
    }
}
