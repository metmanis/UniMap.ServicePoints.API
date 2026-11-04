using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Export;
using UniMap.ServicePoints.API.Gaps;
using UniMap.ServicePoints.API.Here;
using UniMap.ServicePoints.Tests.Gaps;
using UniMap.ServicePoints.Tests.Support;
using static UniMap.ServicePoints.Tests.Gaps.GapShapes;

namespace UniMap.ServicePoints.Tests.Export;

/// <summary>
/// Some tests here change process-wide state (the default culture and TMPDIR), so no other test class may run beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessWideStateCollection
{
    public const string Name = "Tests that change process-wide state";
}

[Collection(ProcessWideStateCollection.Name)]
public class GapExportTests(ScratchDatabase db, WebApplicationFactory<Program> factory)
    : IClassFixture<ScratchDatabase>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Area = """{"type":"Polygon","coordinates":[[[33.0,39.0],[33.2,39.0],[33.2,39.2],[33.0,39.2],[33.0,39.0]]]}""";
    private static readonly string[] Header = ["GapIndex", "AreaKm2", "Vehicle", "DriveTimeMinutes", "DistanceKm", "WktGeometry"];

    private readonly ServicePointSeed _seed = new(db);

    public Task InitializeAsync() => _seed.Clear();

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client(IHereRoutingClient here) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
        b.ConfigureTestServices(s => s.AddSingleton(here));
    }).CreateClient();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Body(string minutes = "30", string km = "null") =>
        $$"""{"area":{{Area}},"vehicle":"PassengerCar","driveTimeMinutes":{{minutes}},"distanceKm":{{km}}}""";

    private async Task AddPoint(int id, double x, double y = 39.1)
    {
        await _seed.Point(id, $"P{id}", x, y);
        await _seed.VehicleType(id, "PassengerCar");
    }

    private static ScriptedHere NoHere() => new(NetTopologySuite.Geometries.MultiPolygon (NetTopologySuite.Geometries.Coordinate _, RangeType _, int _) =>
        throw new InvalidOperationException("HERE must not be called in this test."));

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task No_reachable_area_gives_the_same_422_on_export_and_no_file(string format)
    {
        await AddPoint(17, 33.05);
        var here = new ScriptedHere((_, _, _) => Gf.CreateMultiPolygon());

        var response = await Client(here).PostAsync($"/api/gaps/export?format={format}", Json(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("service point 17", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Excluded_points_do_not_change_the_export_rows()
    {
        var here = await TwoPieces();
        await _seed.Point(2, "No geometry", hasGeometry: false);
        await _seed.VehicleType(2, "PassengerCar");

        var response = await Client(here).PostAsync("/api/gaps/export?format=csv", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', Header), lines[0].Trim().TrimStart('\uFEFF'));
        Assert.Equal(3, lines.Length); // the header and two pieces
    }

    /// <summary>A point whose reach covers the middle of the area, which leaves two gap pieces: west and east.</summary>
    private async Task<ScriptedHere> TwoPieces()
    {
        await AddPoint(1, 33.1);
        return new ScriptedHere((_, _, _) => Rect(33.07, 38.9, 33.13, 39.3));
    }

    private async Task<double> ExpectedAreaKm2(string wkt)
    {
        await using var connection = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT ST_Area(ST_Segmentize(ST_GeomFromText(@wkt, 4326), 0.01)::geography) / 1000000.0", connection);
        cmd.Parameters.AddWithValue("wkt", wkt);
        return (double)(await cmd.ExecuteScalarAsync())!;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; i++;
            }
            else field.Append(c);
        }
        Assert.False(quoted, "The CSV ends inside a quoted field.");
        Assert.Empty(field.ToString());
        Assert.Empty(row);
        return rows;
    }

    private static async IAsyncEnumerable<GapExportRow> Rows(params GapExportRow[] rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }

    // ---- (a) CSV ----

    [Fact]
    public async Task Csv_has_the_header_row_and_one_row_per_gap_piece()
    {
        var response = await Client(await TwoPieces()).PostAsync("/api/gaps/export?format=csv", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("coverage-gaps.csv", response.Content.Headers.ContentDisposition?.FileName);

        var rows = ParseCsv(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, rows.Count);
        Assert.Equal(Header, rows[0]);
        Assert.All(rows, r => Assert.Equal(6, r.Count));

        Assert.Equal(["1", "2"], rows.Skip(1).Select(r => r[0]));
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal("PassengerCar", r[2]);
            Assert.Equal("30", r[3]);
            Assert.Equal("", r[4]); // no distance threshold: blank
            Assert.StartsWith("POLYGON((", r[5]);
        });

        // Longitude first, in the order the pieces come from the result: west of the reach, then east.
        Assert.InRange(double.Parse(rows[1][5]["POLYGON((".Length..].Split(' ')[0], CultureInfo.InvariantCulture), 33.0, 33.07);
        Assert.InRange(double.Parse(rows[2][5]["POLYGON((".Length..].Split(' ')[0], CultureInfo.InvariantCulture), 33.13, 33.2);

        // The exported area is the measure of the 1 km2 rule, not another one.
        foreach (var row in rows.Skip(1))
        {
            var expected = await ExpectedAreaKm2(row[5]);
            Assert.Equal(expected, double.Parse(row[1], CultureInfo.InvariantCulture), expected * 1e-9);
        }
    }

    [Fact]
    public async Task Csv_leaves_the_threshold_that_is_not_set_blank_and_fills_the_other()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(33.07, 38.9, 33.13, 39.3));

        var response = await Client(here).PostAsync("/api/gaps/export?format=csv", Json(Body(minutes: "null", km: "20")));

        var rows = ParseCsv(await response.Content.ReadAsStringAsync());
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal("", r[3]);
            Assert.Equal("20", r[4]);
        });
    }

    [Fact]
    public async Task Csv_quotes_a_wkt_field_that_contains_commas_and_keeps_the_whole_wkt()
    {
        var wkt = "POLYGON((33 39,33.1 39,33.1 39.1,33 39))";
        var stream = new MemoryStream();

        await CsvGapWriter.WriteAsync(stream, Rows(new GapExportRow(1, 12.5, "PassengerCar", 30, null, wkt)), default);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal($"GapIndex,AreaKm2,Vehicle,DriveTimeMinutes,DistanceKm,WktGeometry\r\n1,12.5,PassengerCar,30,,\"{wkt}\"\r\n", text);
    }

    [Fact]
    public async Task Csv_holds_the_full_wkt_however_long()
    {
        var wkt = "POLYGON((" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"33.{i:D8} 39.{i:D8}")) + "))";
        Assert.True(wkt.Length > ExcelGapWriter.MaxCellCharacters);
        var stream = new MemoryStream();

        await CsvGapWriter.WriteAsync(stream, Rows(new GapExportRow(1, 12.5, "PassengerCar", 30, null, wkt)), default);

        var rows = ParseCsv(Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Equal(wkt, rows[1][5]);
    }

    // ---- (b) XLSX ----

    [Fact]
    public async Task Xlsx_reopens_with_the_same_columns_and_the_values_as_typed_cells()
    {
        var response = await Client(await TwoPieces()).PostAsync("/api/gaps/export?format=xlsx", Json(Body(km: "20")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("coverage-gaps.xlsx", response.Content.Headers.ContentDisposition?.FileName);

        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        var sheet = workbook.Worksheet(1);
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal(6, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(Header, Enumerable.Range(1, 6).Select(c => sheet.Cell(1, c).GetString()));

        for (var r = 2; r <= 3; r++)
        {
            Assert.Equal(XLDataType.Number, sheet.Cell(r, 1).DataType);
            Assert.Equal(r - 1, sheet.Cell(r, 1).GetDouble());
            Assert.Equal(XLDataType.Number, sheet.Cell(r, 2).DataType);
            var wkt = sheet.Cell(r, 6).GetString();
            Assert.StartsWith("POLYGON((", wkt);
            var expected = await ExpectedAreaKm2(wkt);
            Assert.Equal(expected, sheet.Cell(r, 2).GetDouble(), expected * 1e-9);
            Assert.Equal("PassengerCar", sheet.Cell(r, 3).GetString());
            Assert.Equal(XLDataType.Number, sheet.Cell(r, 4).DataType);
            Assert.Equal(30, sheet.Cell(r, 4).GetDouble());
            Assert.Equal(XLDataType.Number, sheet.Cell(r, 5).DataType);
            Assert.Equal(20, sheet.Cell(r, 5).GetDouble());
        }
    }

    [Fact]
    public async Task Xlsx_leaves_an_unset_threshold_blank()
    {
        var stream = new MemoryStream();

        await ExcelGapWriter.WriteAsync(stream, Rows(new GapExportRow(1, 12.5, "HeavyDuty", null, 20, "POLYGON((33 39,33.1 39,33.1 39.1,33 39))")), default);

        using var workbook = new XLWorkbook(new MemoryStream(stream.ToArray()));
        var sheet = workbook.Worksheet(1);
        Assert.True(sheet.Cell(2, 4).IsEmpty());
        Assert.Equal(20, sheet.Cell(2, 5).GetDouble());
        Assert.Equal("HeavyDuty", sheet.Cell(2, 3).GetString());
    }

    [Fact]
    public async Task Xlsx_never_truncates_a_wkt_that_exceeds_the_cell_limit_and_says_so_instead()
    {
        var tooLong = "POLYGON((" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"33.{i:D8} 39.{i:D8}")) + "))";
        var fits = new string('x', ExcelGapWriter.MaxCellCharacters);
        Assert.True(tooLong.Length > ExcelGapWriter.MaxCellCharacters);
        var stream = new MemoryStream();

        await ExcelGapWriter.WriteAsync(stream, Rows(
            new GapExportRow(1, 1, "PassengerCar", 30, null, tooLong),
            new GapExportRow(2, 1, "PassengerCar", 30, null, fits)), default);

        using var workbook = new XLWorkbook(new MemoryStream(stream.ToArray()));
        var sheet = workbook.Worksheet(1);
        var note = sheet.Cell(2, 6).GetString();
        Assert.Equal($"[WKT too long for Excel: {tooLong.Length} characters. Use the CSV export.]", note);
        Assert.DoesNotContain("POLYGON", note);
        Assert.Equal(fits, sheet.Cell(3, 6).GetString()); // exactly at the limit is kept whole
    }

    // ---- culture ----

    [Fact]
    public async Task Numbers_use_a_dot_under_a_comma_decimal_culture_in_both_formats()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        var turkish = new CultureInfo("tr-TR");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = turkish;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = turkish;
        try
        {
            Assert.Equal(",", turkish.NumberFormat.NumberDecimalSeparator);
            var row = new GapExportRow(1, 1234.5678, "PassengerCar", 30, 20, "POLYGON((33.5 39.5,33.6 39.5,33.6 39.6,33.5 39.5))");

            var csv = new MemoryStream();
            await CsvGapWriter.WriteAsync(csv, Rows(row), default);
            var csvRow = ParseCsv(Encoding.UTF8.GetString(csv.ToArray()))[1];
            Assert.Equal("1234.5678", csvRow[1]);

            var xlsx = new MemoryStream();
            await ExcelGapWriter.WriteAsync(xlsx, Rows(row), default);
            using var workbook = new XLWorkbook(new MemoryStream(xlsx.ToArray()));
            Assert.Equal(1234.5678, workbook.Worksheet(1).Cell(2, 2).GetDouble());

            // Through the endpoint as well, where the area comes from PostGIS.
            var response = await Client(await TwoPieces()).PostAsync("/api/gaps/export?format=csv", Json(Body()));
            var endpointRows = ParseCsv(await response.Content.ReadAsStringAsync());
            Assert.All(endpointRows.Skip(1), r =>
            {
                Assert.DoesNotContain(',', r[1]);
                Assert.Contains('.', r[1]);
                Assert.True(double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            });
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture) = previous;
        }
    }

    // ---- (c) failures return the normal JSON and no file ----

    private static async Task<JsonElement> AssertJsonNotFile(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        return body;
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task Insufficient_data_returns_422_and_no_file(string format)
    {
        var response = await Client(NoHere()).PostAsync($"/api/gaps/export?format={format}", Json(Body()));

        var body = await AssertJsonNotFile(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("Not enough spatial data", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task A_bad_body_returns_400_and_no_file(string format)
    {
        var response = await Client(NoHere()).PostAsync($"/api/gaps/export?format={format}", Json("{not json"));

        var body = await AssertJsonNotFile(response, HttpStatusCode.BadRequest);
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_threshold_the_service_rejects_returns_400_and_no_file()
    {
        var response = await Client(NoHere()).PostAsync("/api/gaps/export?format=csv", Json(Body(minutes: "61")));

        var body = await AssertJsonNotFile(response, HttpStatusCode.BadRequest);
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task A_here_failure_returns_503_and_no_file(string format)
    {
        await AddPoint(1, 33.05);
        var here = new ScriptedHere(NetTopologySuite.Geometries.MultiPolygon (NetTopologySuite.Geometries.Coordinate _, RangeType _, int _) => throw new HereUnavailableException("rejected"));

        var response = await Client(here).PostAsync($"/api/gaps/export?format={format}", Json(Body()));

        var body = await AssertJsonNotFile(response, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("HERE", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?format=")]
    [InlineData("?format=pdf")]
    [InlineData("?format=csv&format=xlsx")]
    public async Task A_missing_or_unknown_format_returns_400_and_no_file(string query)
    {
        var response = await Client(NoHere()).PostAsync("/api/gaps/export" + query, Json(Body()));

        var body = await AssertJsonNotFile(response, HttpStatusCode.BadRequest);
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    // ---- no gaps ----

    [Fact]
    public async Task No_gaps_gives_a_csv_with_the_header_row_only()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(32, 38, 34, 40));

        var response = await Client(here).PostAsync("/api/gaps/export?format=csv", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["GapIndex,AreaKm2,Vehicle,DriveTimeMinutes,DistanceKm,WktGeometry"], ParseCsv(await response.Content.ReadAsStringAsync()).Select(r => string.Join(',', r)));
    }

    [Fact]
    public async Task No_gaps_gives_an_xlsx_with_the_header_row_only()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(32, 38, 34, 40));

        var response = await Client(here).PostAsync("/api/gaps/export?format=xlsx", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        var sheet = workbook.Worksheet(1);
        Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal(Header, Enumerable.Range(1, 6).Select(c => sheet.Cell(1, c).GetString()));
    }

    // ---- (d) nothing is written to disk ----

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task An_export_writes_no_file_to_disk(string format)
    {
        // Path.GetTempPath reads TMPDIR on every call, so a fresh empty directory shows any temp file the export makes.
        var scratch = Directory.CreateTempSubdirectory("gap-export-tmp-");
        var previousTmp = Environment.GetEnvironmentVariable("TMPDIR");
        var previousTmpWin = Environment.GetEnvironmentVariable("TMP");
        var previousTempWin = Environment.GetEnvironmentVariable("TEMP");
        var client = Client(await TwoPieces()); // built first so host start-up is not under observation
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", scratch.FullName);
            Environment.SetEnvironmentVariable("TMP", scratch.FullName); // Windows reads TMP and TEMP
            Environment.SetEnvironmentVariable("TEMP", scratch.FullName);
            Assert.StartsWith(scratch.FullName, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var response = await client.PostAsync($"/api/gaps/export?format={format}", Json(Body()));
            var bytes = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(bytes);
            Assert.Empty(Directory.GetFileSystemEntries(scratch.FullName, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", previousTmp);
            Environment.SetEnvironmentVariable("TMP", previousTmpWin);
            Environment.SetEnvironmentVariable("TEMP", previousTempWin);
            scratch.Delete(true);
        }
    }

    // ---- the read of the rows happens before any header ----

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task A_database_failure_while_reading_the_rows_returns_json_and_no_file(string format)
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(33.07, 38.9, 33.13, 39.3));
        // The gap is calculated against the scratch database. Only the export read goes to a server that cannot be reached.
        await using var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Database=none;Timeout=1;Pooling=false");
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IHereRoutingClient>(here);
                s.AddScoped(_ => new GapExportService(unreachable));
            });
        }).CreateClient();

        var response = await client.PostAsync($"/api/gaps/export?format={format}", Json(Body()));

        var body = await AssertJsonNotFile(response, HttpStatusCode.InternalServerError);
        Assert.Equal(GapExportService.ReadFailedMessage, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_gap_served_from_the_cache_keeps_srid_4326_for_the_area_query()
    {
        await AddPoint(1, 33.1);
        var here = new ScriptedHere((_, _, _) => Rect(33.07, 38.9, 33.13, 39.3));
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ServicePoints", db.ConnectionString);
            b.ConfigureTestServices(s => s.AddSingleton<IHereRoutingClient>(here));
        });
        var request = new GapRequest(RectPolygon(33.0, 39.0, 33.2, 39.2), VehicleType.PassengerCar, 30, null);

        using var scope = app.Services.CreateScope();
        var gapService = scope.ServiceProvider.GetRequiredService<IGapService>();
        var fresh = await gapService.CalculateAsync(request, default);
        var cached = await gapService.CalculateAsync(request, default);

        Assert.Single(here.Requests); // the second answer came from the cache
        Assert.Equal(4326, fresh.Gaps!.SRID);
        Assert.Equal(4326, cached.Gaps!.SRID);

        var rows = await scope.ServiceProvider.GetRequiredService<GapExportService>().ReadRowsAsync(request, cached.Gaps, default);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var expected = await ExpectedAreaKm2(row.WktGeometry);
            Assert.Equal(expected, row.AreaKm2, expected * 1e-9);
        }
    }
}
