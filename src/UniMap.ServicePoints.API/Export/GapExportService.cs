using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.Gaps;

namespace UniMap.ServicePoints.API.Export;

/// <summary>Turns a calculated gap into export rows, one per piece, read from PostGIS as the rows are written.</summary>
public class GapExportService(NpgsqlDataSource dataSource)
{
    public const string CsvFileName = "coverage-gaps.csv";
    public const string XlsxFileName = "coverage-gaps.xlsx";

    public const string ReadFailedMessage = "The coverage gaps were calculated, but the export could not be prepared. Please try again.";

    /// <summary>
    /// Pieces in the order they come from the MultiPolygon, numbered from 1. An empty MultiPolygon gives no rows.
    /// All rows are read, and the connection is released, before this returns, so a database failure happens
    /// before any response header and a slow download never holds a connection.
    /// </summary>
    public async Task<IReadOnlyList<GapExportRow>> ReadRowsAsync(GapRequest request, MultiPolygon gaps, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT d.n, {GapMeasure.SquareKmSql("d.geom")}, ST_AsText(d.geom)
            FROM ST_Dump(@gaps) WITH ORDINALITY AS d(path, geom, n)
            ORDER BY d.n
            """);
        cmd.Parameters.Add(new NpgsqlParameter("gaps", NpgsqlDbType.Geometry) { Value = gaps });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var vehicle = request.Vehicle.ToString();
        var rows = new List<GapExportRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new GapExportRow(
                (int)reader.GetInt64(0), reader.GetDouble(1), vehicle, request.DriveTimeMinutes, request.DistanceKm, reader.GetString(2)));
        }
        return rows;
    }

    /// <summary>Hands the rows to a writer one at a time.</summary>
    public static async IAsyncEnumerable<GapExportRow> AsStream(IReadOnlyList<GapExportRow> rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }
}
