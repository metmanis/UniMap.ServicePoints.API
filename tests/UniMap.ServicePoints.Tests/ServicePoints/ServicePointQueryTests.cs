using Npgsql;
using UniMap.ServicePoints.API.Domain;
using UniMap.ServicePoints.API.ServicePoints;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.ServicePoints;

public class ServicePointQueryTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);
    private NpgsqlDataSource _dataSource = null!;
    private ServicePointQuery _query = null!;

    // Tests in a class run one at a time on a shared database, so every test starts from empty tables.
    public async Task InitializeAsync()
    {
        await _seed.Clear();
        _dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        _query = new ServicePointQuery(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private static int[] Ids(params int[] ids) => ids;

    private static ServicePointFilter Filter(
        int[]? cityIds = null, int[]? districtIds = null, string[]? brands = null,
        string[]? types = null, VehicleType[]? vehicleTypes = null)
        => new(cityIds, districtIds, brands, types, vehicleTypes);

    private async Task<int[]> FilterIds(ServicePointFilter filter)
        => (await _query.FilterAsync(filter, default)).Select(p => p.Id).Order().ToArray();

    private async Task<string[]> Suggest(string text, int limit = 10)
        => (await _query.SearchByNameAsync(text, limit, default)).Select(p => p.Name!).ToArray();

    [Fact]
    public async Task Brand_filter_compares_normalized_values()
    {
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.LinkBrand(1, await _seed.Brand("Işık"));

        Assert.Equal(Ids(1), await FilterIds(Filter(brands: ["ISIK"])));
    }

    [Fact]
    public async Task Two_brands_in_one_filter_match_either()
    {
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.Point(3, "C");
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));
        await _seed.LinkBrand(2, await _seed.Brand("Işık"));
        await _seed.LinkBrand(3, await _seed.Brand("Other"));

        Assert.Equal(Ids(1, 2), await FilterIds(Filter(brands: ["bosch", "ISIK"])));
    }

    [Fact]
    public async Task Brand_and_vehicle_type_must_both_match()
    {
        var bosch = await _seed.Brand("Bosch");
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.Point(3, "C");
        await _seed.LinkBrand(1, bosch);
        await _seed.LinkBrand(2, bosch);
        await _seed.VehicleType(1, "HeavyDuty");
        await _seed.VehicleType(2, "PassengerCar");
        await _seed.VehicleType(3, "HeavyDuty");

        Assert.Equal(Ids(1), await FilterIds(Filter(brands: ["Bosch"], vehicleTypes: [VehicleType.HeavyDuty])));
    }

    [Fact]
    public async Task Vehicle_type_filter_matches_any_listed_type()
    {
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.Point(3, "C");
        await _seed.VehicleType(1, "HeavyDuty");
        await _seed.VehicleType(2, "PassengerCar");

        Assert.Equal(Ids(1), await FilterIds(Filter(vehicleTypes: [VehicleType.HeavyDuty])));
        Assert.Equal(Ids(1, 2), await FilterIds(Filter(vehicleTypes: [VehicleType.HeavyDuty, VehicleType.PassengerCar])));
    }

    [Fact]
    public async Task Type_filter_compares_exactly()
    {
        await _seed.Point(1, "A", type: "Authorized");
        await _seed.Point(2, "B", type: "authorized");
        await _seed.Point(3, "C", type: "Private");

        Assert.Equal(Ids(1), await FilterIds(Filter(types: ["Authorized"])));
        Assert.Equal(Ids(1, 3), await FilterIds(Filter(types: ["Authorized", "Private"])));
    }

    [Fact]
    public async Task Inactive_and_null_active_points_are_never_returned_or_suggested()
    {
        await _seed.Point(1, "Shell Active");
        await _seed.Point(2, "Shell Inactive", active: false);
        await _seed.Point(3, "Shell Unknown", active: null);
        await _seed.Point(4, "Shell Nowhere", active: false, hasGeometry: false);

        Assert.Equal(Ids(1), await FilterIds(Filter()));
        Assert.Equal(["Shell Active"], await Suggest("shell"));
    }

    // Longitude and latitude at the limits are still a place on the map.
    public static TheoryData<string?> NoValidLocation() =>
        new() { null, "POINT EMPTY", "POINT(200 10)", "POINT(-181 10)", "POINT(10 91)", "POINT(10 -91)" };

    [Theory]
    [MemberData(nameof(NoValidLocation))]
    public async Task A_point_without_a_valid_location_is_suggested_but_never_returned_by_the_filter(string? wkt)
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "Shell Map", x: 2, y: 3);
        await _seed.Point(2, "Shell Nowhere", type: "Authorized");
        await _seed.SetGeometry(2, wkt);

        Assert.Equal(Ids(1), await FilterIds(Filter()));
        Assert.Equal(Ids(1), await FilterIds(Filter(types: ["Authorized"])));
        Assert.Equal(["Shell Map", "Shell Nowhere"], (await Suggest("shell")).Order());
        Assert.False((await _query.SearchByNameAsync("nowhere", 10, default)).Single().HasLocation);
    }

    [Theory]
    [InlineData("POINT(180 90)")]
    [InlineData("POINT(-180 -90)")]
    public async Task Coordinates_at_the_limits_are_a_valid_location(string wkt)
    {
        await _seed.Point(1, "Edge");
        await _seed.SetGeometry(1, wkt);

        Assert.Equal(Ids(1), await FilterIds(Filter()));
        Assert.True(Assert.Single(await _query.SearchByNameAsync("edge", 10, default)).HasLocation);
    }

    [Fact]
    public async Task A_suggestion_without_a_location_has_null_coordinates_and_the_text_columns_as_city_and_district()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10);
        await _seed.Point(1, "Shell", hasGeometry: false, type: "Authorized", address: "Main St 1", code: 42,
            cityText: "Konya", districtText: "Selçuklu");
        await _seed.LinkBrand(1, await _seed.Brand("Zeta"));
        await _seed.LinkBrand(1, await _seed.Brand("Alfa"));

        var s = Assert.Single(await _query.SearchByNameAsync("shell", 10, default));

        Assert.Equal(new ServicePointSuggestion(1, "42", "Shell", "Authorized", "Main St 1", "Konya", "Selçuklu",
            ["Alfa", "Zeta"], null, null, false), s, new SuggestionComparer());
    }

    [Fact]
    public async Task A_suggestion_without_a_location_and_without_text_columns_has_null_city_and_district()
    {
        await _seed.Point(1, "Shell", hasGeometry: false);

        var s = Assert.Single(await _query.SearchByNameAsync("shell", 10, default));

        Assert.Null(s.City);
        Assert.Null(s.District);
    }

    [Fact]
    public async Task A_suggestion_with_a_location_keeps_the_view_names_and_ignores_the_text_columns()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10);
        await _seed.Point(1, "Shell", x: 2.5, y: 3.5, cityText: "Wrong City", districtText: "Wrong District");

        var s = Assert.Single(await _query.SearchByNameAsync("shell", 10, default));

        Assert.Equal(new ServicePointSuggestion(1, null, "Shell", "Authorized", null, "Ankara", "Çankaya",
            [], 3.5, 2.5, true), s, new SuggestionComparer());
    }

    [Fact]
    public async Task An_inactive_point_without_a_location_is_not_suggested()
    {
        await _seed.Point(1, "Shell Nowhere", active: false, hasGeometry: false);
        await _seed.Point(2, "Shell Unknown", active: null, hasGeometry: false);

        Assert.Empty(await Suggest("shell"));
    }

    [Fact]
    public async Task Points_without_a_location_rank_and_count_against_the_limit_like_any_other()
    {
        await _seed.Point(100, "Eski Shell", hasGeometry: false);
        for (var i = 1; i <= 12; i++) await _seed.Point(i, $"Shell {i:00}", hasGeometry: i % 2 == 0);

        var capped = await Suggest("shell", limit: 10);

        Assert.Equal(10, capped.Length);
        Assert.DoesNotContain("Eski Shell", capped);
        Assert.Equal("Eski Shell", (await Suggest("shell", limit: 20)).Last());
    }

    [Fact]
    public async Task No_filters_returns_every_active_point_with_geometry_including_those_without_brands()
    {
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));

        var all = await _query.FilterAsync(Filter(), default);

        Assert.Equal(Ids(1, 2), all.Select(p => p.Id).Order());
        Assert.Empty(all.Single(p => p.Id == 2).Brands);
    }

    [Fact]
    public async Task Empty_lists_and_blank_brands_are_ignored()
    {
        await _seed.Point(1, "A");
        await _seed.Point(2, "B");
        await _seed.LinkBrand(1, await _seed.Brand("Işık"));

        Assert.Equal(Ids(1, 2), await FilterIds(Filter(cityIds: [], brands: ["", "  "])));
        Assert.Equal(Ids(1), await FilterIds(Filter(brands: ["", "isik"])));
    }

    [Fact]
    public async Task Summary_carries_view_names_sorted_brands_and_coordinates()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10);
        await _seed.Point(1, "Shell", x: 2.5, y: 3.5, type: "Authorized", address: "Main St 1", code: 42,
            cityText: "Wrong City", districtText: "Wrong District");
        await _seed.LinkBrand(1, await _seed.Brand("Zeta"));
        await _seed.LinkBrand(1, await _seed.Brand("Alfa"));

        var p = Assert.Single(await _query.FilterAsync(Filter(), default));

        Assert.Equal(new ServicePointSummary(1, "42", "Shell", "Authorized", "Main St 1", "Ankara", "Çankaya",
            ["Alfa", "Zeta"], 3.5, 2.5), p, new SummaryComparer());
    }

    [Fact]
    public async Task City_filter_selects_by_view_id_and_coordinates_win_over_text_columns()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.City(2, "Izmir", 20, 0, 30, 10);
        await _seed.Point(1, "InAnkara", x: 2, y: 2, cityText: "Izmir");
        await _seed.Point(2, "InIzmir", x: 22, y: 2, cityText: "Ankara");
        await _seed.Point(3, "Nowhere", x: 50, y: 50, cityText: "Ankara");

        Assert.Equal(Ids(1), await FilterIds(Filter(cityIds: [1])));
        Assert.Equal(Ids(2), await FilterIds(Filter(cityIds: [2])));
    }

    [Fact]
    public async Task Two_cities_combine_with_or()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.City(2, "Izmir", 20, 0, 30, 10);
        await _seed.City(3, "Bursa", 40, 0, 50, 10);
        await _seed.Point(1, "A", x: 2, y: 2);
        await _seed.Point(2, "B", x: 22, y: 2);
        await _seed.Point(3, "C", x: 42, y: 2);

        Assert.Equal(Ids(1, 2), await FilterIds(Filter(cityIds: [1, 2])));
    }

    [Fact]
    public async Task District_filter_selects_by_view_id_and_coordinates_win_over_text_columns()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "West", 0, 0, 5, 10);
        await _seed.District(12, "East", 5, 0, 10, 10);
        await _seed.Point(1, "InWest", x: 2, y: 2, districtText: "East");
        await _seed.Point(2, "InEast", x: 8, y: 2, districtText: "West");

        Assert.Equal(Ids(1), await FilterIds(Filter(districtIds: [11])));
        Assert.Equal(Ids(1, 2), await FilterIds(Filter(districtIds: [11, 12])));
    }

    [Fact]
    public async Task Fields_combine_with_and()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "A", x: 2, y: 2, type: "Authorized");
        await _seed.Point(2, "B", x: 2, y: 2, type: "Private");
        await _seed.Point(3, "C", x: 50, y: 50, type: "Authorized");

        Assert.Equal(Ids(1), await FilterIds(Filter(cityIds: [1], types: ["Authorized"])));
    }

    [Fact]
    public async Task One_character_or_blank_query_returns_nothing()
    {
        await _seed.Point(1, "Shell");

        Assert.Empty(await Suggest("s"));
        Assert.Empty(await Suggest("  s  "));
        Assert.Empty(await Suggest("   "));
        Assert.Empty(await Suggest(""));
    }

    [Fact]
    public async Task Percent_is_a_literal_not_a_wildcard()
    {
        await _seed.Point(1, "a%z shop");
        await _seed.Point(2, "Alpha Zulu");

        Assert.Equal(["a%z shop"], await Suggest("a%z"));
    }

    [Fact]
    public async Task Underscore_and_backslash_are_literals_not_wildcards()
    {
        await _seed.Point(1, "a_b");
        await _seed.Point(2, "axb");
        await _seed.Point(3, @"back\slash");

        Assert.Equal(["a_b"], await Suggest("a_b"));
        Assert.Equal([@"back\slash"], await Suggest(@"k\s"));
    }

    [Fact]
    public async Task Two_letter_prefix_finds_a_longer_name()
    {
        await _seed.Point(1, "Shell Servis");

        Assert.Equal(["Shell Servis"], await Suggest("sh"));
    }

    [Fact]
    public async Task Misspelled_longer_query_still_matches_through_similarity()
    {
        await _seed.Point(1, "Shell Servis");
        await _seed.Point(2, "Kaya Otomotiv");

        Assert.Equal(["Shell Servis"], await Suggest("Shel Servis"));
    }

    [Fact]
    public async Task Turkish_uppercase_query_matches_regardless_of_case_and_accent()
    {
        await _seed.Point(1, "Şell");
        await _seed.Point(2, "SELL");
        await _seed.Point(3, "Kaya Otomotiv");

        var names = await Suggest("ŞELL");

        Assert.Equal(["SELL", "Şell"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Suggestions_are_capped_and_prefix_matches_rank_first()
    {
        await _seed.Point(100, "Eski Shell");
        for (var i = 1; i <= 12; i++) await _seed.Point(i, $"Shell {i:00}");

        var capped = await Suggest("shell", limit: 10);
        var all = await Suggest("shell", limit: 20);

        Assert.Equal(10, capped.Length);
        Assert.DoesNotContain("Eski Shell", capped);
        Assert.Equal(13, all.Length);
        Assert.Equal("Eski Shell", all.Last());
    }

    [Fact]
    public async Task Suggestions_include_summary_fields()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "Shell", x: 2, y: 3);
        await _seed.LinkBrand(1, await _seed.Brand("Shell"));

        var p = Assert.Single(await _query.SearchByNameAsync("shell", 10, default));

        Assert.Equal("Ankara", p.City);
        Assert.Equal(["Shell"], p.Brands);
        Assert.Equal(3, p.Latitude);
        Assert.True(p.HasLocation);
    }

    [Fact]
    public async Task Name_search_has_a_gin_trigram_index_on_the_normalized_name()
    {
        var def = await _seed.Scalar<string>("""
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'Services' AND tablename = 'ServicePoints' AND indexdef ILIKE '%gin_trgm_ops%'
            """);

        Assert.NotNull(def);
        Assert.Contains("USING gin", def);
        Assert.Contains("normalize_text", def);
    }

    [Fact]
    public async Task Filter_options_list_sorted_names_and_active_types()
    {
        await _seed.City(1, "Izmir", 20, 0, 30, 10);
        await _seed.City(2, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10);
        await _seed.Brand("Zeta");
        await _seed.Brand("Alfa");
        await _seed.Point(1, "A", type: "Private");
        await _seed.Point(2, "B", type: "Authorized");
        await _seed.Point(3, "C", type: "Dealer", active: false);
        await _seed.Point(4, "D", type: null);
        await _seed.Point(5, "E", type: "Ghost", hasGeometry: false);
        await _seed.Point(6, "F", type: "Offshore", x: 200);

        var o = await _query.GetFilterOptionsAsync(default);

        Assert.Equal(["Ankara", "Izmir"], o.Cities.Select(c => c.Name));
        Assert.Equal(Ids(2, 1), o.Cities.Select(c => c.Id));
        Assert.Equal(["Çankaya"], o.Districts.Select(d => d.Name));
        Assert.Equal(["Alfa", "Zeta"], o.Brands);
        Assert.Equal(["Authorized", "Private"], o.Types);
        Assert.Equal(["HeavyDuty", "PassengerCar"], o.VehicleTypes);
    }

    [Fact]
    public async Task Filter_options_say_which_city_each_district_is_in()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(11, "Çankaya", 0, 0, 5, 10, cityId: 1);
        await _seed.District(12, "Nowhere", 50, 50, 51, 51);

        var o = await _query.GetFilterOptionsAsync(default);

        Assert.Equal(1, o.Districts.Single(d => d.Name == "Çankaya").CityId);
        Assert.Null(o.Districts.Single(d => d.Name == "Nowhere").CityId);
    }

    private sealed class SuggestionComparer : IEqualityComparer<ServicePointSuggestion>
    {
        public bool Equals(ServicePointSuggestion? a, ServicePointSuggestion? b) =>
            a is not null && b is not null
            && a.Id == b.Id && a.Code == b.Code && a.Name == b.Name && a.Type == b.Type && a.Address == b.Address
            && a.City == b.City && a.District == b.District && a.Latitude == b.Latitude && a.Longitude == b.Longitude
            && a.HasLocation == b.HasLocation && a.Brands.SequenceEqual(b.Brands);

        public int GetHashCode(ServicePointSuggestion s) => s.Id;
    }

    private sealed class SummaryComparer : IEqualityComparer<ServicePointSummary>
    {
        public bool Equals(ServicePointSummary? a, ServicePointSummary? b) =>
            a is not null && b is not null
            && a.Id == b.Id && a.Code == b.Code && a.Name == b.Name && a.Type == b.Type && a.Address == b.Address
            && a.City == b.City && a.District == b.District && a.Latitude == b.Latitude && a.Longitude == b.Longitude
            && a.Brands.SequenceEqual(b.Brands);

        public int GetHashCode(ServicePointSummary s) => s.Id;
    }
}
