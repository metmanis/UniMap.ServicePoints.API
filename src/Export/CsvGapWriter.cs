using System.Globalization;
using System.Text;

namespace UniMap.ServicePoints.API.Export;

/// <summary>RFC 4180 CSV, written row by row so the file is never held whole.</summary>
public static class CsvGapWriter
{
    public static readonly string[] Columns = ["GapIndex", "AreaKm2", "Vehicle", "DriveTimeMinutes", "DistanceKm", "WktGeometry"];

    public static async Task WriteAsync(Stream output, IAsyncEnumerable<GapExportRow> rows, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\r\n";
        await writer.WriteLineAsync(string.Join(',', Columns));

        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',',
                row.GapIndex.ToString(CultureInfo.InvariantCulture),
                row.AreaKm2.ToString("R", CultureInfo.InvariantCulture),
                Field(row.Vehicle),
                row.DriveTimeMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.DistanceKm?.ToString(CultureInfo.InvariantCulture) ?? "",
                Field(row.WktGeometry)));
        }
    }

    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
