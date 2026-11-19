using System.Text.Json.Nodes;
using Content.Sync.Search;
using Content.Tests.Support;
using MongoDB.Bson;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Content.Tests.Sync;

/// <summary>What a sync run turns stored hotels into, including messy ones, and how the index is described.</summary>
[Collection(ClassMapCollection.Name)]
public class SyncDocumentTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SyncDocumentTests() => _ = TestMappings.Counter;

    private static BsonDocument RichHotel(long id) => new()
    {
        { "_id", id },
        { "Code", $"AH{id}" },
        { "Name", $"Hotel {id}" },
        { "CityCode", "C001" },
        { "CountryCode", "XX" },
        { "Rating", 4.0 },
        { "Latitude", 10.5 },
        { "Longitude", 20.25 },
        {
            "Providers", new BsonArray
            {
                new BsonDocument { { "Supplier", "supplier-a" }, { "HotelId", id } },
                new BsonDocument { { "Supplier", "supplier-b" }, { "HotelId", $"B-{id}" } },
                new BsonDocument { { "Supplier", "supplier-a" }, { "HotelId", id } },
                new BsonDocument { { "Supplier", "supplier-c" }, { "HotelId", BsonNull.Value } },
            }
        },
    };

    [Fact]
    public async Task A_run_flattens_provider_keys_and_location_into_the_search_document()
    {
        var h = new SyncHarness();
        h.Source.Add(SyncHarness.Collection, [RichHotel(42), RichHotel(43)]);

        await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        var document = h.Index.Documents(h.IndexName())["42"];
        document.Name.ShouldBe("Hotel 42");
        document.CityCode.ShouldBe("C001");
        document.ProviderKeys.ShouldBe(["supplier-a:42", "supplier-b:B-42"], "duplicates and entries without an id are left out");
        document.Location!.Latitude.ShouldBe(10.5);
        document.Location.Longitude.ShouldBe(20.25);
    }

    [Fact]
    public async Task Messy_documents_are_synced_with_the_bad_fields_dropped_and_counted()
    {
        var messy = RichHotel(1);
        messy["Rating"] = "3.5";
        messy["Amenities"] = new BsonDocument("a", 1);
        messy["Images"] = new BsonArray { new BsonDocument("Url", "https://example.com/x.jpg"), 17 };
        var h = new SyncHarness();
        h.Source.Add(SyncHarness.Collection, [messy, RichHotel(2)]);
        var before = TestMappings.Counter.Total;

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(2);
        summary.DeadLettered.ShouldBe(0);
        var document = h.Index.Documents(h.IndexName())["1"];
        document.Rating.ShouldBe(3.5);
        document.Amenities.ShouldBeEmpty();
        document.Images.ShouldBe(["https://example.com/x.jpg"]);
        (TestMappings.Counter.Total - before).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void The_index_is_described_with_an_explicit_strict_mapping()
    {
        var client = new OpenSearchClient(new ConnectionSettings(new Uri("http://localhost:9200")));
        var request = new CreateIndexDescriptor("content-test")
            .Settings(s => s.NumberOfShards(1).NumberOfReplicas(0))
            .Map<SearchDocument>(HotelIndexMapping.Describe);

        var body = JsonNode.Parse(client.RequestResponseSerializer.SerializeToString(request))!;

        var mappings = body["mappings"]!;
        mappings["dynamic"]!.GetValue<string>().ShouldBe("strict");
        var properties = mappings["properties"]!;
        properties["providerKeys"]!["type"]!.GetValue<string>().ShouldBe("keyword");
        properties["location"]!["type"]!.GetValue<string>().ShouldBe("geo_point");
        properties["cityCode"]!["type"]!.GetValue<string>().ShouldBe("keyword");
        properties["name"]!["type"]!.GetValue<string>().ShouldBe("text");
        properties["id"]!["type"]!.GetValue<string>().ShouldBe("keyword");
        body["settings"]!.ToJsonString().ShouldContain("\"index.number_of_replicas\":0");
    }
}

[Collection(ClassMapCollection.Name)]
public class SyncParallelTests
{
    private const string CollectionA = "hotel_supplier-a";
    private const string CollectionB = "hotel_supplier-b";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SyncParallelTests() => _ = TestMappings.Counter;

    private static SyncHarness TwoCollections()
    {
        var h = new SyncHarness();
        h.Source.Add(CollectionA, InMemorySource.Hotels(Enumerable.Range(1, 700).Select(i => (long)i)));
        h.Source.Add(CollectionB, InMemorySource.Hotels(Enumerable.Range(5000, 450).Select(i => (long)i)));
        return h;
    }

    [Fact]
    public async Task Two_collections_sync_in_parallel_each_into_its_own_index_with_its_own_checkpoint()
    {
        var h = TwoCollections();
        var supplierBCalls = 0;
        h.Writer.ThrowWhen = (_, index) => index.EndsWith("supplier-b", StringComparison.Ordinal) && Interlocked.Increment(ref supplierBCalls) >= 3
            ? new SearchIndexException("supplier-b index failure")
            : null;
        var options = h.Fast(100);

        var failure = await Should.ThrowAsync<AggregateException>(() => h.Runner.RunManyAsync([CollectionA, CollectionB], options, _ct));

        failure.InnerExceptions.ShouldHaveSingleItem().Message.ShouldBe("supplier-b index failure");
        h.Index.Ids("content-" + CollectionA).Count.ShouldBe(700, "the healthy collection finished despite its sibling failing");
        h.Checkpoints.Current(CollectionA).ShouldBeNull();
        var checkpointB = h.Checkpoints.Current(CollectionB);
        checkpointB.ShouldNotBeNull();
        h.Index.Ids("content-" + CollectionB).Count.ShouldBe((int)checkpointB.AsInt64 - 4999, "supplier-b is exactly as far as its own checkpoint says");

        h.Writer.ThrowWhen = null;
        await h.Runner.RunManyAsync([CollectionB], options, _ct);
        h.Index.Ids("content-" + CollectionB).ShouldBe(Enumerable.Range(5000, 450).Select(i => i.ToString()), ignoreOrder: true);
    }

    [Fact]
    public async Task Both_collections_complete_when_nothing_fails()
    {
        var h = TwoCollections();

        var summaries = await h.Runner.RunManyAsync([CollectionA, CollectionB], h.Fast(100), _ct);

        summaries.Select(s => s.Indexed).ShouldBe([700L, 450L]);
        h.Index.Ids("content-" + CollectionA).Count.ShouldBe(700);
        h.Index.Ids("content-" + CollectionB).Count.ShouldBe(450);
    }

    [Fact]
    public async Task Listing_the_same_collection_twice_runs_it_once()
    {
        var h = TwoCollections();

        var summaries = await h.Runner.RunManyAsync([CollectionA, CollectionA], h.Fast(100), _ct);

        summaries.Count.ShouldBe(1);
    }
}

[Collection(ClassMapCollection.Name)]
public class SearchDocumentRoundTripTests
{
    public SearchDocumentRoundTripTests() => _ = TestMappings.Counter;

    [Fact]
    public void A_hotel_mapped_to_a_search_document_and_back_keeps_everything_a_read_needs()
    {
        var hotel = ReadsHarness.Hotel(42);

        var document = HotelSearchDocumentMapper.Map(hotel);
        HotelSearchDocumentMapper.TryToHotel(document, out var back).ShouldBeTrue();

        back.Id.ShouldBe(42);
        back.Name.ShouldBe(hotel.Name);
        back.Latitude.ShouldBe(10.5);
        back.Longitude.ShouldBe(20.25);
        back.Rooms.Select(r => (r.Id, r.GroupId, r.Name)).ShouldBe([("R1", "7", "Double")]);
        back.Providers.Select(p => (p.Supplier, p.HotelId)).ShouldBe([("supplier-a", "42")]);
        document.ProviderKeys.ShouldBe(["supplier-a:42"]);
    }

    [Fact]
    public void A_search_document_whose_id_is_not_a_number_cannot_become_a_hotel() =>
        HotelSearchDocumentMapper.TryToHotel(new SearchDocument { Id = "not-a-number" }, out _).ShouldBeFalse();

    [Fact]
    public void Rooms_and_providers_are_kept_in_the_stored_source_but_not_indexed()
    {
        var client = new OpenSearchClient(new ConnectionSettings(new Uri("http://localhost:9200")));
        var request = new CreateIndexDescriptor("content-test").Map<SearchDocument>(HotelIndexMapping.Describe);

        var properties = JsonNode.Parse(client.RequestResponseSerializer.SerializeToString(request))!["mappings"]!["properties"]!;

        properties["rooms"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        properties["providers"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
    }
}
