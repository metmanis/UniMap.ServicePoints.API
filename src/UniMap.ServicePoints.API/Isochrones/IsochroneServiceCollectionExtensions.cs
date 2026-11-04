using Npgsql;

namespace UniMap.ServicePoints.API.Isochrones;

public static class IsochroneServiceCollectionExtensions
{
    public const string ConnectionStringName = "ServicePoints";

    public static IServiceCollection AddIsochrones(this IServiceCollection services)
    {
        // Built on first use, so hosts and tests that never touch the database start without a connection string.
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseNetTopologySuite();
            return builder.Build();
        });
        services.AddScoped<IIsochroneService, IsochroneService>();
        return services;
    }
}
