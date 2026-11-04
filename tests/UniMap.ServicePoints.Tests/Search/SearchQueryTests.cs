using Npgsql;
using UniMap.ServicePoints.API.Search;
using UniMap.ServicePoints.Tests.Support;

namespace UniMap.ServicePoints.Tests.Search;

public class SearchQueryTests(ScratchDatabase db) : IClassFixture<ScratchDatabase>, IAsyncLifetime
{
    private readonly ServicePointSeed _seed = new(db);
    private NpgsqlDataSource _dataSource = null!;
    private SearchQuery _query = null!;

    // Tests in a class run one at a time on a shared database, so every test starts from empty tables.
    public async Task InitializeAsync()
    {
        await _seed.Clear();
        _dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        _query = new SearchQuery(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private Task<IReadOnlyList<SearchResult>> Search(string text) => _query.SearchAsync(text, default);

    private async Task<ServicePointSearchResult[]> Points(string text)
        => (await Search(text)).OfType<ServicePointSearchResult>().ToArray();

    private async Task<int[]> PointIds(string text) => (await Points(text)).Select(p => p.Id).ToArray();

    private static int[] Ids(params int[] ids) => ids;

    private static string NameOf(SearchResult result) => result switch
    {
        ServicePointSearchResult p => p.Name!,
        CitySearchResult c => c.Name,
        DistrictSearchResult d => d.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    [Theory]
    [InlineData("shell")]
    [InlineData("ankara")]
    [InlineData("cankaya")]
    [InlineData("bosch")]
    public async Task Finds_by_any_field(string text)
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(1, "Çankaya", 0, 0, 10, 10, cityId: 1);
        await _seed.Point(1, "Shell Servis", x: 2, y: 2);
        await _seed.Point(2, "Other", x: 50, y: 50);
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));

        Assert.Equal(Ids(1), await PointIds(text));
    }

    [Fact]
    public async Task Words_can_match_different_fields()
    {
        await _seed.City(1, "Bursa", 0, 0, 10, 10);
        await _seed.City(2, "Ankara", 20, 0, 30, 10);
        var ford = await _seed.Brand("Ford");
        var opel = await _seed.Brand("Opel");
        await _seed.Point(1, "Alpha", x: 2, y: 2);
        await _seed.Point(2, "Beta", x: 3, y: 3);
        await _seed.Point(3, "Gamma", x: 22, y: 2);
        await _seed.LinkBrand(1, ford);
        await _seed.LinkBrand(2, opel);
        await _seed.LinkBrand(3, ford);

        var results = await Search("bursa ford");

        Assert.Equal(1, Assert.Single(results).Id);
    }

    [Theory]
    [InlineData("brusa", "Bursa")]
    [InlineData("frod", "Ford Otomotiv")]
    [InlineData("bursaa", "Bursa")]
    [InlineData("mersi", "Mersin")]
    public async Task Finds_typos(string text, string expected)
    {
        await _seed.City(1, "Bursa", 0, 0, 10, 10);
        await _seed.City(2, "Mersin", 20, 0, 30, 10);
        await _seed.Point(1, "Ford Otomotiv", x: 50, y: 50);

        var result = Assert.Single(await Search(text));

        Assert.Equal(expected, NameOf(result));
    }

    [Fact]
    public async Task A_3_letter_word_allows_one_edit_only()
    {
        await _seed.Point(1, "Ford");

        Assert.Equal(Ids(1), await PointIds("fod"));
        Assert.Empty(await PointIds("fxy"));
        Assert.Empty(await PointIds("fxd")); // Exactly 2 edits from "ford".
    }

    [Fact]
    public async Task A_field_word_over_255_characters_is_not_compared_by_edits()
    {
        await _seed.Point(1, new string('a', 256) + " Ford");
        await _seed.Point(2, new string('b', 256));

        // Postgres's levenshtein raises an error on words this long, which would fail the whole search.
        Assert.Equal(Ids(1), await PointIds("frod"));
        Assert.Empty(await PointIds("xyzw"));
    }

    [Fact]
    public async Task Turkish_text_is_normalized()
    {
        await _seed.District(1, "Şişli", 0, 0, 10, 10);

        var district = Assert.IsType<DistrictSearchResult>(Assert.Single(await Search("sisli")));

        Assert.Equal("Şişli", district.Name);
    }

    [Fact]
    public async Task Orders_exact_then_prefix_then_contains_then_other()
    {
        await _seed.Point(1, "Acme");
        await _seed.Point(2, "Xx Shell");
        await _seed.Point(3, "Shell Yy");
        await _seed.Point(4, "Shell");
        await _seed.LinkBrand(1, await _seed.Brand("Shell"));

        Assert.Equal(Ids(4, 3, 2, 1), await PointIds("shell"));
    }

    [Fact]
    public async Task A_city_ranks_above_a_point_with_the_same_name()
    {
        await _seed.Point(1, "Bursa", x: 50, y: 50);
        await _seed.District(1, "Bursa", 0, 0, 10, 10);
        await _seed.City(1, "Bursa", 0, 0, 10, 10);

        var results = await Search("bursa");

        Assert.Collection(results,
            r => Assert.IsType<CitySearchResult>(r),
            r => Assert.IsType<DistrictSearchResult>(r),
            r => Assert.IsType<ServicePointSearchResult>(r));
    }

    [Fact]
    public async Task How_the_name_matches_outranks_the_kind()
    {
        await _seed.City(1, "Bursa Merkez", 0, 0, 10, 10);
        await _seed.Point(1, "Bursa", x: 50, y: 50);

        var results = await Search("bursa");

        Assert.Collection(results,
            r => Assert.IsType<ServicePointSearchResult>(r),
            r => Assert.IsType<CitySearchResult>(r));
    }

    [Fact]
    public async Task Ties_go_to_the_more_similar_name_then_the_lower_id()
    {
        await _seed.Point(1, "Shell Aaaaaaaaaa Bbbbbbbbbb");
        await _seed.Point(2, "Shell Aa");
        await _seed.Point(3, "Shell Aa");

        Assert.Equal(Ids(2, 3, 1), await PointIds("shell"));
    }

    [Fact]
    public async Task A_name_match_ranks_above_a_brand_or_place_match()
    {
        await _seed.City(1, "Ford", 0, 0, 10, 10);
        await _seed.Point(1, "Alpha", x: 50, y: 50);
        await _seed.Point(2, "Beta", x: 2, y: 2);
        await _seed.Point(3, "Mega Ford", x: 51, y: 51);
        await _seed.LinkBrand(1, await _seed.Brand("Ford"));

        Assert.Equal(Ids(3, 1, 2), await PointIds("ford"));
    }

    [Fact]
    public async Task Limits_each_kind()
    {
        for (var i = 1; i <= 5; i++)
        {
            await _seed.City(i, $"Bursa {i}", 0, 0, 10, 10);
            await _seed.District(i, $"Bursa {i}", 0, 0, 10, 10);
        }
        for (var i = 1; i <= 11; i++) await _seed.Point(i, $"Bursa Servis {i}", x: 50, y: 50);
        await _seed.Point(12, "Bursa", x: 50, y: 50);

        var results = await Search("bursa");

        Assert.Equal(SearchQuery.MaxCities, results.OfType<CitySearchResult>().Count());
        Assert.Equal(SearchQuery.MaxDistricts, results.OfType<DistrictSearchResult>().Count());
        var points = results.OfType<ServicePointSearchResult>().ToArray();
        Assert.Equal(SearchQuery.MaxServicePoints, points.Length);
        Assert.Equal(12, points[0].Id);
        Assert.Equal((3, 3, 10), (SearchQuery.MaxCities, SearchQuery.MaxDistricts, SearchQuery.MaxServicePoints));
    }

    [Fact]
    public async Task Inactive_points_are_excluded()
    {
        await _seed.Point(1, "Shell Open");
        await _seed.Point(2, "Shell Closed", active: false);
        await _seed.Point(3, "Shell Unknown", active: null);

        Assert.Equal(Ids(1), await PointIds("shell"));
    }

    [Fact]
    public async Task A_point_without_a_location_is_included_using_its_text_columns()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.Point(1, "Shell Nowhere", hasGeometry: false, cityText: "Konya", districtText: "Selçuklu");
        await _seed.Point(2, "Shell Located", x: 2, y: 2, cityText: "Konya");

        var point = Assert.Single(await Points("konya"));

        Assert.Equal(1, point.Id);
        Assert.False(point.HasLocation);
        Assert.Null(point.Latitude);
        Assert.Null(point.Longitude);
        Assert.Equal("Konya", point.City);
        Assert.Equal("Selçuklu", point.District);
        Assert.Equal(Ids(1), await PointIds("selcuklu"));
    }

    [Fact]
    public async Task A_located_point_reports_its_coordinates_and_place()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(1, "Çankaya", 0, 0, 10, 10, cityId: 1);
        await _seed.Point(1, "Shell", x: 2, y: 3, address: "Main St 1", cityText: "Konya", districtText: "Selçuklu");
        await _seed.LinkBrand(1, await _seed.Brand("Şahin"));
        await _seed.LinkBrand(1, await _seed.Brand("Bosch"));

        var point = Assert.Single(await Points("shell"));

        Assert.True(point.HasLocation);
        Assert.Equal(3, point.Latitude);
        Assert.Equal(2, point.Longitude);
        Assert.Equal("Main St 1", point.Address);
        Assert.Equal("Ankara", point.City);
        Assert.Equal("Çankaya", point.District);
        Assert.Equal(["Bosch", "Şahin"], point.Brands);
    }

    [Fact]
    public async Task A_district_reports_its_city_name_or_null()
    {
        await _seed.City(1, "Ankara", 0, 0, 10, 10);
        await _seed.District(1, "Merkez", 0, 0, 10, 10, cityId: 1);
        await _seed.District(2, "Merkez", 20, 0, 30, 10);

        var districts = (await Search("merkez")).Cast<DistrictSearchResult>().ToArray();

        Assert.Equal([1, 2], districts.Select(d => d.Id));
        Assert.Equal("Ankara", districts[0].CityName);
        Assert.Null(districts[1].CityName);
    }

    [Theory]
    [InlineData("%%", "Servis %% Bir")]
    [InlineData("__", "Servis __ Bir")]
    [InlineData("s%l", "Servis s%l Bir")]
    [InlineData("s_l", "Servis s_l Bir")]
    public async Task Percent_and_underscore_match_literally(string text, string literal)
    {
        await _seed.Point(1, literal);
        // Would match if the character acted as a wildcard, and is too far off to match any other way.
        await _seed.Point(2, "Basilika");

        Assert.Equal(Ids(1), await PointIds(text));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a b")]
    public async Task Only_short_words_returns_nothing(string text)
    {
        await _seed.City(1, "a b", 0, 0, 10, 10);
        await _seed.Point(1, "a b");

        Assert.Empty(await Search(text));
    }

    [Fact]
    public async Task A_short_word_next_to_a_longer_one_is_ignored()
    {
        await _seed.Point(1, "Shell");

        Assert.Equal(Ids(1), await PointIds("x shell"));
    }
}
