using Microsoft.Extensions.Options;
using UniMap.ServicePoints.API.Boundaries;
using UniMap.ServicePoints.API.Data;
using UniMap.ServicePoints.API.Distances;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Endpoints;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Isochrones;
using UniMap.ServicePoints.API.Routes;
using UniMap.ServicePoints.API.Search;
using UniMap.ServicePoints.API.ServicePoints;

if (args.Contains(MigrationRunner.Flag))
    return await MigrationRunner.RunAsync(args, Console.Out, Console.Error);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<ServicePointLimits>()
    .BindConfiguration(ServicePointLimits.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ServicePointLimits>, ServicePointLimitsValidator>();
builder.Services.AddHereClient();
builder.Services.AddIsochrones();
builder.Services.AddGaps();
builder.Services.AddDistances();
builder.Services.AddRoutes();
builder.Services.AddScoped<ServicePointQuery>();
builder.Services.AddScoped<SearchQuery>();
builder.Services.AddOptions<BoundaryOptions>()
    .BindConfiguration(BoundaryOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BoundaryOptions>, BoundaryOptionsValidator>();
builder.Services.AddScoped<BoundaryQuery>();
var app = builder.Build();

app.MapConfigEndpoints();
app.MapServicePointEndpoints();
app.MapBoundaryEndpoints();
app.MapGapEndpoints();
app.MapDistanceEndpoints();
app.MapRouteEndpoints();
app.MapSearchEndpoints();

app.Run();
return 0;

public partial class Program;
