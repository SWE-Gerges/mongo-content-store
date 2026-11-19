using Content.Mongo.Repository;
using Content.Store;
using Content.Store.Configuration;
using Content.Store.Models;
using Content.Store.Rooms;
using Content.Tests.Support;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;

namespace Content.Tests.Store;

[Collection(ClassMapCollection.Name)]
public class HotelContentStoreTests
{
    private readonly IMongoRepository<HotelDocument> _repository = Substitute.For<IMongoRepository<HotelDocument>>();
    private static readonly IReadOnlyList<HotelDocument> None = new List<HotelDocument>();

    private readonly HotelContentStore _store;

    public HotelContentStoreTests()
    {
        _ = TestMappings.Counter;
        var options = Options.Create(new ContentStoreOptions { Suppliers = ["supplier-a", "supplier-b"], MaxIdsPerRequest = 5, MaxPageSize = 50, DefaultPageSize = 10, MaxSearchResults = 20 });
        _store = new HotelContentStore(
            _repository,
            new HotelCollectionNaming(),
            new RoomExtraMapperRegistry([new SupplierARoomExtraMapper()]),
            options);
    }

    private void StubFilter() =>
        _repository.FilterAsync(
                Arg.Any<string>(),
                Arg.Any<FilterDefinition<HotelDocument>>(),
                Arg.Any<QueryOptions<HotelDocument>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(None);

    private void StubPage(KeysetPage<HotelDocument> page) =>
        _repository.PageByIdAsync(
                Arg.Any<string>(),
                Arg.Any<BsonValue?>(),
                Arg.Any<int>(),
                Arg.Any<Func<HotelDocument, BsonValue>>(),
                Arg.Any<FilterDefinition<HotelDocument>?>(),
                Arg.Any<ProjectionDefinition<HotelDocument>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(page);

    private static BsonDocument Render(FilterDefinition<HotelDocument> filter) =>
        filter.Render(new RenderArgs<HotelDocument>(BsonSerializer.SerializerRegistry.GetSerializer<HotelDocument>(), BsonSerializer.SerializerRegistry));

    private FilterDefinition<HotelDocument> CapturedFilter(int callIndex = 0)
    {
        var call = _repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMongoRepository<>.FilterAsync) && c.GetArguments()[1] is FilterDefinition<HotelDocument>)
            .ElementAt(callIndex);
        return (FilterDefinition<HotelDocument>)call.GetArguments()[1]!;
    }

    private QueryOptions<HotelDocument>? CapturedOptions() =>
        (QueryOptions<HotelDocument>?)_repository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMongoRepository<>.FilterAsync) && c.GetArguments()[1] is FilterDefinition<HotelDocument>)
            .GetArguments()[2];

    [Fact]
    public async Task GetByIds_queries_the_numeric_id_with_in_never_a_stringified_comparison()
    {
        StubFilter();

        await _store.GetByIdsAsync("supplier-a", [3, 1, 2, 3], TestContext.Current.CancellationToken);

        var rendered = Render(CapturedFilter());
        var inValues = rendered["_id"]["$in"].AsBsonArray;
        inValues.Select(v => v.BsonType).ShouldAllBe(t => t == BsonType.Int64);
        inValues.Select(v => v.AsInt64).ShouldBe([3L, 1L, 2L]);
        rendered.ToString().ShouldNotContain("$expr");
        rendered.ToString().ShouldNotContain("$toString");
        await _repository.Received().FilterAsync(
            "hotel_supplier-a",
            Arg.Any<FilterDefinition<HotelDocument>>(),
            Arg.Any<QueryOptions<HotelDocument>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIds_rejects_unknown_suppliers_and_oversized_requests()
    {
        await Should.ThrowAsync<UnknownSupplierException>(() => _store.GetByIdsAsync("supplier-z", [1], TestContext.Current.CancellationToken));
        await Should.ThrowAsync<UnknownSupplierException>(() => _store.GetByIdsAsync("../hotels", [1], TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidQueryException>(() => _store.GetByIdsAsync("supplier-a", [1, 2, 3, 4, 5, 6], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetByCodes_uses_in_on_code_and_skips_blank_codes()
    {
        StubFilter();

        await _store.GetByCodesAsync("supplier-b", ["AB12", " ", "AB12", "CD34"], TestContext.Current.CancellationToken);

        Render(CapturedFilter())["Code"]["$in"].AsBsonArray.Select(v => v.AsString).ShouldBe(["AB12", "CD34"]);
    }

    [Fact]
    public async Task Lookups_by_id_and_by_code_carry_the_configured_server_side_time_limit()
    {
        StubFilter();

        await _store.GetByIdsAsync("supplier-a", [1, 2], TestContext.Current.CancellationToken);
        CapturedOptions()!.MaxTime.ShouldBe(TimeSpan.FromSeconds(5));
        _repository.ClearReceivedCalls();
        await _store.GetByCodesAsync("supplier-a", ["AB12"], TestContext.Current.CancellationToken);

        CapturedOptions()!.MaxTime.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetByCodes_with_only_blank_codes_does_not_query()
    {
        var result = await _store.GetByCodesAsync("supplier-b", [" "], TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        _repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetByCity_projects_only_the_id_and_derives_the_next_cursor_from_limit_plus_one()
    {
        StubPage(new KeysetPage<HotelDocument>([new HotelDocument { Id = 10 }, new HotelDocument { Id = 20 }], HasMore: true, new BsonInt64(20)));

        var page = await _store.GetByCityAsync("supplier-a", "C001", 2, cursor: null, TestContext.Current.CancellationToken);

        page.Ids.ShouldBe([10L, 20L]);
        page.NextCursor.ShouldNotBeNull();
        HotelCursor.Decode(page.NextCursor).ShouldBe(20);
        var call = _repository.ReceivedCalls().Single();
        call.GetArguments()[0].ShouldBe("hotel_supplier-a");
        call.GetArguments()[1].ShouldBeNull();
        call.GetArguments()[2].ShouldBe(2);
        var projection = (ProjectionDefinition<HotelDocument>)call.GetArguments()[5]!;
        projection.Render(new RenderArgs<HotelDocument>(BsonSerializer.LookupSerializer<HotelDocument>(), BsonSerializer.SerializerRegistry))
            .ShouldBe(new BsonDocument("_id", 1));
    }

    [Fact]
    public async Task GetByCity_on_the_last_page_has_no_cursor_and_passes_the_decoded_cursor_on()
    {
        StubPage(new KeysetPage<HotelDocument>([new HotelDocument { Id = 31 }], HasMore: false, new BsonInt64(31)));

        var page = await _store.GetByCityAsync("supplier-a", "C001", null, HotelCursor.Encode(30), TestContext.Current.CancellationToken);

        page.NextCursor.ShouldBeNull();
        var call = _repository.ReceivedCalls().Single();
        call.GetArguments()[1].ShouldBe(new BsonInt64(30));
        call.GetArguments()[2].ShouldBe(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public async Task GetByCity_rejects_page_sizes_outside_the_configured_range(int size) =>
        await Should.ThrowAsync<InvalidQueryException>(() => _store.GetByCityAsync("supplier-a", "C001", size, null, TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetByCity_rejects_a_garbage_cursor() =>
        await Should.ThrowAsync<InvalidCursorException>(() => _store.GetByCityAsync("supplier-a", "C001", 5, "not-a-cursor!", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Anchored_search_is_a_case_sensitive_escaped_prefix_regex()
    {
        StubFilter();

        await _store.SearchByNameAsync("Grand (Hotel)", "XX", NameSearchMode.Anchored, null, TestContext.Current.CancellationToken);

        var rendered = Render(CapturedFilter());
        var regex = rendered["Name"].AsBsonRegularExpression;
        regex.Pattern.ShouldBe(@"^Grand\ \(Hotel\)");
        regex.Options.ShouldBeEmpty();
        rendered["CountryCode"].AsString.ShouldBe("XX");
        CapturedOptions()!.Collation.ShouldBeNull();
        CapturedOptions()!.Limit.ShouldBe(20);
    }

    [Fact]
    public async Task Contains_search_is_an_unanchored_case_insensitive_regex_with_a_time_limit()
    {
        StubFilter();

        await _store.SearchByNameAsync("a.b", null, NameSearchMode.Contains, 5, TestContext.Current.CancellationToken);

        var regex = Render(CapturedFilter())["Name"].AsBsonRegularExpression;
        regex.Pattern.ShouldBe(@"a\.b");
        regex.Options.ShouldBe("i");
        CapturedOptions()!.MaxTime.ShouldNotBeNull();
        CapturedOptions()!.Limit.ShouldBe(5);
    }

    [Fact]
    public async Task Case_insensitive_search_is_an_equality_match_with_an_en_strength_2_collation()
    {
        StubFilter();

        await _store.SearchByNameAsync("grand example", null, NameSearchMode.CaseInsensitive, null, TestContext.Current.CancellationToken);

        Render(CapturedFilter())["Name"].AsString.ShouldBe("grand example");
        var collation = CapturedOptions()!.Collation!;
        collation.Locale.ShouldBe("en");
        collation.Strength.ShouldBe(CollationStrength.Secondary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_requires_a_name(string name) =>
        await Should.ThrowAsync<InvalidQueryException>(() => _store.SearchByNameAsync(name, null, NameSearchMode.Anchored, null, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Search_rejects_over_long_names_and_oversized_limits()
    {
        await Should.ThrowAsync<InvalidQueryException>(() => _store.SearchByNameAsync(new string('a', 101), null, NameSearchMode.Anchored, null, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidQueryException>(() => _store.SearchByNameAsync("abc", null, NameSearchMode.Anchored, 21, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindByProvider_matches_one_array_element_on_supplier_and_both_id_forms()
    {
        StubFilter();

        await _store.FindByProviderAsync("supplier-a", "1001", TestContext.Current.CancellationToken);

        var elemMatch = Render(CapturedFilter())["Providers"]["$elemMatch"].AsBsonDocument;
        elemMatch["Supplier"].AsString.ShouldBe("supplier-a");
        var variants = elemMatch["HotelId"]["$in"].AsBsonArray;
        variants.Count.ShouldBe(2);
        variants[0].ShouldBe(new BsonString("1001"));
        variants[1].ShouldBe(new BsonInt64(1001));
    }

    [Theory]
    [InlineData("B-77")]
    [InlineData("007")]
    [InlineData("1e3")]
    public async Task FindByProvider_only_adds_the_numeric_form_for_canonical_integers(string id)
    {
        StubFilter();

        await _store.FindByProviderAsync("supplier-b", id, TestContext.Current.CancellationToken);

        Render(CapturedFilter())["Providers"]["$elemMatch"]["HotelId"]["$in"].AsBsonArray.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RoomDetail_reports_each_outcome_distinctly()
    {
        var hotel = new HotelDocument
        {
            Id = 5,
            Rooms = [new RoomData { Id = "R1", Name = "Double", Extra = new BsonDocument("Pax", 2) }],
        };
        _repository.GetByIdAsync<long>("hotel_supplier-a", 5, null, Arg.Any<CancellationToken>()).Returns(hotel);
        _repository.GetByIdAsync<long>("hotel_supplier-b", 5, null, Arg.Any<CancellationToken>()).Returns(hotel);
        _repository.GetByIdAsync<long>("hotel_supplier-a", 6, null, Arg.Any<CancellationToken>()).Returns((HotelDocument?)null);

        var found = await _store.GetRoomDetailAsync("supplier-a", 5, "R1", TestContext.Current.CancellationToken);
        found.Status.ShouldBe(RoomDetailStatus.Found);
        found.Detail!.Occupancy.ShouldBe(2);

        (await _store.GetRoomDetailAsync("supplier-a", 5, "R404", TestContext.Current.CancellationToken)).Status.ShouldBe(RoomDetailStatus.RoomNotFound);
        (await _store.GetRoomDetailAsync("supplier-a", 6, "R1", TestContext.Current.CancellationToken)).Status.ShouldBe(RoomDetailStatus.HotelNotFound);
        (await _store.GetRoomDetailAsync("supplier-b", 5, "R1", TestContext.Current.CancellationToken)).Status.ShouldBe(RoomDetailStatus.NoMapper);
    }
}

public class HotelCursorTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(9_007_199_254_740_993L)]
    [InlineData(long.MaxValue)]
    [InlineData(-5L)]
    public void Round_trips_any_long(long id) =>
        HotelCursor.Decode(HotelCursor.Encode(id)).ShouldBe(id);

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("aGVsbG8")]
    public void Rejects_garbage(string cursor) =>
        Should.Throw<InvalidCursorException>(() => HotelCursor.Decode(cursor));
}

public class CollectionNamingTests
{
    private readonly HotelCollectionNaming _naming = new();

    [Fact]
    public void Names_follow_the_hotel_prefix_strategy_and_need_no_supplier_enum()
    {
        _naming.Master.ShouldBe("hotels");
        _naming.ForSupplier("supplier-a").ShouldBe("hotel_supplier-a");
        _naming.ForSupplier("supplier-d").ShouldBe("hotel_supplier-d");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Supplier-A")]
    [InlineData("a b")]
    [InlineData("a/../b")]
    [InlineData("-a")]
    [InlineData("a--b")]
    [InlineData("$where")]
    public void Rejects_keys_that_could_escape_the_naming_scheme(string key) =>
        Should.Throw<ArgumentException>(() => _naming.ForSupplier(key));
}
