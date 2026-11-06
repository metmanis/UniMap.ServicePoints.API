using ClosedXML.Excel;

namespace UniMap.ServicePoints.API.Export;

/// <summary>
/// ClosedXML has no streaming writer, so the workbook is built in memory and then saved to the stream.
/// It is saved to a MemoryStream and copied to the output, so no file is created.
/// </summary>
public static class ExcelGapWriter
{
    /// <summary>The most characters Excel holds in one cell.</summary>
    public const int MaxCellCharacters = 32767;

    public static string TooLongNote(int length) => $"[WKT too long for Excel: {length} characters. Use the CSV export.]";

    public static async Task WriteAsync(Stream output, IAsyncEnumerable<GapExportRow> rows, CancellationToken ct)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Coverage gaps");
        for (var c = 0; c < CsvGapWriter.Columns.Length; c++)
            sheet.Cell(1, c + 1).Value = CsvGapWriter.Columns[c];

        var r = 1;
        await foreach (var row in rows.WithCancellation(ct))
        {
            r++;
            sheet.Cell(r, 1).Value = row.GapIndex;
            sheet.Cell(r, 2).Value = row.AreaKm2;
            sheet.Cell(r, 3).Value = row.Vehicle;
            if (row.DriveTimeMinutes is { } minutes) sheet.Cell(r, 4).Value = minutes;
            if (row.DistanceKm is { } km) sheet.Cell(r, 5).Value = km;
            // Cut-off WKT is corrupt geometry that looks valid, so an over-long one is replaced by a note, never truncated.
            sheet.Cell(r, 6).Value = row.WktGeometry.Length > MaxCellCharacters ? TooLongNote(row.WktGeometry.Length) : row.WktGeometry;
        }

        // The package needs a seekable stream and a response body is not one, so it is saved to memory first.
        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        await buffer.CopyToAsync(output, ct);
    }
}
