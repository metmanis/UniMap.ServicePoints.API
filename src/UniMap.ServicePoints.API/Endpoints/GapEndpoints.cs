using NetTopologySuite.Geometries;
using Npgsql;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Export;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.API.Isochrones;

namespace UniMap.ServicePoints.API.Endpoints;

public static class GapEndpoints
{
    private const string HereUnavailableMessage =
        "The HERE routing service is unavailable, so coverage gaps could not be calculated. No partial result is returned.";

    /// <summary>The request and its gaps on success. Otherwise the error response, with nothing else started.</summary>
    private record Calculation(IResult? Failure, GapRequest? Request, MultiPolygon? Gaps, int ExcludedPointCount = 0);

    public static IEndpointRouteBuilder MapGapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/gaps", async (HttpRequest http, IGapService gaps, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var calculation = await CalculateAsync(http, gaps, loggers, ct);
            return calculation.Failure ?? Results.Json(new { gaps = calculation.Gaps, excludedPointCount = calculation.ExcludedPointCount }, GapRequestParser.GeoJsonOptions);
        });

        app.MapPost("/api/gaps/export", async (
            HttpRequest http, IGapService gaps, GapExportService export, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var format = http.Query["format"].ToString();
            var isCsv = format.Equals("csv", StringComparison.OrdinalIgnoreCase) && http.Query["format"].Count == 1;
            var isXlsx = format.Equals("xlsx", StringComparison.OrdinalIgnoreCase) && http.Query["format"].Count == 1;
            if (!isCsv && !isXlsx)
                return Results.BadRequest(new { errors = new[] { "format must be csv or xlsx." } });

            // Calculated before any response header, so a failure returns the usual JSON and no file.
            var calculation = await CalculateAsync(http, gaps, loggers, ct);
            if (calculation.Failure is not null) return calculation.Failure;

            // The rows are read before the file result is returned, so a database failure is a JSON error and not a broken file.
            IReadOnlyList<GapExportRow> list;
            try
            {
                list = await export.ReadRowsAsync(calculation.Request!, calculation.Gaps!, ct);
            }
            catch (NpgsqlException ex)
            {
                loggers.CreateLogger(nameof(GapEndpoints)).LogError(ex, "Reading the gap export rows failed");
                return Results.Json(new { message = GapExportService.ReadFailedMessage }, statusCode: StatusCodes.Status500InternalServerError);
            }

            var rows = GapExportService.AsStream(list);
            return isCsv
                ? Results.Stream(s => CsvGapWriter.WriteAsync(s, rows, ct), "text/csv", GapExportService.CsvFileName)
                : Results.Stream(s => ExcelGapWriter.WriteAsync(s, rows, ct),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", GapExportService.XlsxFileName);
        });

        return app;
    }

    private static async Task<Calculation> CalculateAsync(HttpRequest http, IGapService gaps, ILoggerFactory loggers, CancellationToken ct)
    {
        var parsed = await GapRequestParser.ParseAsync(http.Body, ct);
        if (parsed.Request is null)
            return new Calculation(Results.BadRequest(new { errors = parsed.Errors }), null, null);

        try
        {
            var result = await gaps.CalculateAsync(parsed.Request, ct);
            return result.IsCalculated
                ? new Calculation(null, parsed.Request, result.Gaps, result.ExcludedPointCount)
                : new Calculation(Results.UnprocessableEntity(new { message = result.Message }), null, null);
        }
        catch (GapValidationException ex)
        {
            return new Calculation(Results.BadRequest(new { errors = ex.Errors }), null, null);
        }
        catch (NoReachableAreaException ex)
        {
            return new Calculation(Results.UnprocessableEntity(new { message = ex.Message }), null, null);
        }
        catch (GapCalculationUnstableException ex)
        {
            loggers.CreateLogger(nameof(GapEndpoints)).LogWarning("Gap calculation stopped: {Reason}", ex.Message);
            return new Calculation(Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable), null, null);
        }
        catch (HereUnavailableException ex)
        {
            // The reason stays in the log: it can name configuration keys. It never holds an API key or a URL.
            loggers.CreateLogger(nameof(GapEndpoints)).LogWarning("Gap calculation stopped because HERE failed: {Reason}", ex.Message);
            return new Calculation(Results.Json(new { message = HereUnavailableMessage }, statusCode: StatusCodes.Status503ServiceUnavailable), null, null);
        }
    }
}
