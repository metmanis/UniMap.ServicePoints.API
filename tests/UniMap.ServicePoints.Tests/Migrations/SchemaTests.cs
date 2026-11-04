using Npgsql;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Migrations;

public class SchemaTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>
{
    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task Exec(string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<string?> PrimaryKey(string table, string schema = "Services") => Scalar<string>($"""
        SELECT string_agg(a.attname, ',' ORDER BY k.ord)
        FROM pg_constraint con
        JOIN pg_class t ON t.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
        WHERE con.contype = 'p' AND n.nspname = '{schema}' AND t.relname = '{table}'
        """);

    [Fact]
    public async Task ServicePoints_has_IsStale_not_null_default_false()
    {
        var row = await Scalar<string>("""
            SELECT data_type || '|' || is_nullable || '|' || column_default
            FROM information_schema.columns
            WHERE table_schema = 'Services' AND table_name = 'ServicePoints' AND column_name = 'IsStale'
            """);
        Assert.Equal("boolean|NO|false", row);
    }

    [Fact]
    public async Task ServicePoints_has_primary_key_on_Id()
        => Assert.Equal("Id", await PrimaryKey("ServicePoints"));

    [Fact]
    public async Task ServicePoints_IsActive_is_left_unchanged()
    {
        var row = await Scalar<string>("""
            SELECT data_type || '|' || is_nullable || '|' || coalesce(column_default, 'none')
            FROM information_schema.columns
            WHERE table_schema = 'Services' AND table_name = 'ServicePoints' AND column_name = 'IsActive'
            """);
        Assert.Equal("boolean|YES|none", row);
    }

    [Theory]
    [InlineData("Brands", "Brands", "Id")]
    [InlineData("Services", "ServicePointsBrands", "ServicePointId,BrandId")]
    [InlineData("Services", "ServicePointVehicleTypes", "ServicePointId,VehicleType")]
    [InlineData("Services", "ServicePointIsochrones", "ServicePointId,VehicleType,RangeType,RangeValue")]
    public async Task New_tables_exist_with_expected_primary_key(string schema, string table, string expectedKey)
        => Assert.Equal(expectedKey, await PrimaryKey(table, schema));

    [Theory]
    [InlineData("Services", "Brands")]
    [InlineData("Services", "BrandsServicePoints")]
    public async Task The_old_brand_table_names_do_not_exist(string schema, string table)
        => Assert.Null(await Scalar<string>($"SELECT to_regclass('\"{schema}\".\"{table}\"')::text"));

    [Fact]
    public async Task Brands_Id_is_identity_integer_and_Name_is_unique()
    {
        var idCol = await Scalar<string>("""
            SELECT data_type || '|' || is_identity FROM information_schema.columns
            WHERE table_schema = 'Brands' AND table_name = 'Brands' AND column_name = 'Id'
            """);
        Assert.Equal("integer|YES", idCol);

        await Exec("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('dup-test')""");
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => Exec("""INSERT INTO "Brands"."Brands" ("Name") VALUES ('dup-test')"""));
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task Isochrone_geometry_column_is_MultiPolygon_4326()
    {
        var row = await Scalar<string>("""
            SELECT type || '|' || srid FROM geometry_columns
            WHERE f_table_schema = 'Services' AND f_table_name = 'ServicePointIsochrones' AND f_geometry_column = 'Geometry'
            """);
        Assert.Equal("MULTIPOLYGON|4326", row);
    }

    private const string Poly = "ST_GeomFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))', 4326)";

    private static int _nextId = 1000;
    private static int NextId() => Interlocked.Increment(ref _nextId);

    private Task InsertServicePoint(int id)
        => Exec($"""INSERT INTO "Services"."ServicePoints" ("Id", "Geometry") VALUES ({id}, ST_SetSRID(ST_MakePoint(32.8, 39.9), 4326))""");

    [Theory]
    [InlineData("PassengerCar", true)]
    [InlineData("HeavyDuty", true)]
    [InlineData("Truck", false)]
    [InlineData("car", false)]
    public async Task ServicePointVehicleTypes_VehicleType_is_restricted(string value, bool accepted)
    {
        var id = NextId();
        await InsertServicePoint(id);
        var insert = () => Exec($"""INSERT INTO "Services"."ServicePointVehicleTypes" VALUES ({id}, '{value}')""");
        if (accepted) await insert();
        else Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(insert)).SqlState);
    }

    [Theory]
    [InlineData("car", "time", true)]
    [InlineData("truck", "distance", true)]
    [InlineData("bus", "time", false)]
    [InlineData("PassengerCar", "time", false)]
    [InlineData("car", "area", false)]
    [InlineData("car", "Time", false)]
    public async Task ServicePointIsochrones_VehicleType_and_RangeType_are_restricted(string vehicle, string range, bool accepted)
    {
        var id = NextId();
        await InsertServicePoint(id);
        var insert = () => Exec($"""
            INSERT INTO "Services"."ServicePointIsochrones"
            VALUES ({id}, '{vehicle}', '{range}', 300, {Poly}, now())
            """);
        if (accepted) await insert();
        else Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(insert)).SqlState);
    }

    [Fact]
    public async Task Foreign_keys_reject_unknown_service_point()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            """INSERT INTO "Services"."ServicePointVehicleTypes" VALUES (999999, 'PassengerCar')"""));
        Assert.Equal("23503", ex.SqlState);
    }

    [Theory]
    [InlineData("ServicePoints", "Geometry")]
    [InlineData("ServicePointIsochrones", "Geometry")]
    public async Task GIST_index_exists_on_geometry(string table, string column)
    {
        var count = await Scalar<long>($"""
            SELECT count(*) FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class ix ON ix.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ix.relam
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0]
            WHERE n.nspname = 'Services' AND t.relname = '{table}' AND am.amname = 'gist' AND a.attname = '{column}'
            """);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("City")]
    [InlineData("District")]
    [InlineData("Type")]
    public async Task Btree_index_exists_on_filter_columns(string column)
    {
        var count = await Scalar<long>($"""
            SELECT count(*) FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class ix ON ix.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ix.relam
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0]
            WHERE n.nspname = 'Services' AND t.relname = 'ServicePoints' AND am.amname = 'btree' AND a.attname = '{column}'
            """);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Scratch_database_name_has_required_prefix()
        => Assert.StartsWith("unimap_sp_test_", db.Name);
}
