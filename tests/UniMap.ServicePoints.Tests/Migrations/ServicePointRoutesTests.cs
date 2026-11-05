using Npgsql;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

public class ServicePointRoutesTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);

    // Tests in a class run one at a time on a shared database; deleting the points cascades to the routes.
    public async Task InitializeAsync()
    {
        await _seed.Clear();
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task Route(int from, int to) => _seed.Exec($"""
        INSERT INTO "Services"."ServicePointRoutes"
            ("FromServicePointId", "ToServicePointId", "FromX", "FromY", "ToX", "ToY", "Geometry", "DistanceMeters", "DurationSeconds", "CalculatedAt")
        VALUES ({from}, {to}, 1, 1, 2, 2, ST_SetSRID(ST_MakeLine(ST_MakePoint(1, 1), ST_MakePoint(2, 2)), 4326), 1500.5, 120, now())
        """);

    private Task<long> RouteCount(string where = "true") =>
        _seed.Scalar<long>($"""SELECT count(*) FROM "Services"."ServicePointRoutes" WHERE {where}""");

    [Fact]
    public async Task A_route_row_can_be_saved_with_a_LineString()
    {
        await Route(1, 2);

        Assert.Equal("LINESTRING", await _seed.Scalar<string>(
            """SELECT GeometryType("Geometry") FROM "Services"."ServicePointRoutes" WHERE "FromServicePointId" = 1"""));
        Assert.Equal(120, await _seed.Scalar<int>(
            """SELECT "DurationSeconds" FROM "Services"."ServicePointRoutes" WHERE "FromServicePointId" = 1"""));
    }

    [Fact]
    public async Task Each_direction_is_a_separate_row()
    {
        await Route(1, 2);
        await Route(2, 1);

        Assert.Equal(2, await RouteCount());
    }

    [Fact]
    public async Task A_second_row_for_the_same_direction_is_rejected()
    {
        await Route(1, 2);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Route(1, 2));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_route_from_a_point_to_itself_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Route(1, 1));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Deleting_a_point_deletes_its_routes(int deletedPoint)
    {
        await Route(1, 2);
        await Route(2, 1);

        await _seed.Exec($"""DELETE FROM "Services"."ServicePoints" WHERE "Id" = {deletedPoint}""");

        Assert.Equal(0, await RouteCount());
    }

    [Fact]
    public async Task The_fuzzystrmatch_extension_is_available()
    {
        Assert.Equal(2, await _seed.Scalar<int>("SELECT levenshtein('brusa', 'bursa')"));
    }
}
