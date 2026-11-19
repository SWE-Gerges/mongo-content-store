using Content.Reads;
using Content.Store;
using Content.Store.Models;
using Content.Sync.Search;
using Content.Tests.Support;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using NSubstitute;

namespace Content.Tests.Reads;

[Collection(ClassMapCollection.Name)]
public class ContentReaderTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public ContentReaderTests() => _ = TestMappings.Counter;

    private static long[] Ids(ReadResult<HotelDocument> result) => [.. result.Items.Select(h => h.Id).Order()];

    [Fact]
    public async Task Ids_the_index_has_are_served_from_the_index_alone()
    {
        var h = new ReadsHarness().IndexHas(1, 2, 3);

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1, 2, 3], _ct);

        result.Source.ShouldBe(ContentSource.Index);
        Ids(result).ShouldBe([1, 2, 3]);
        h.AskedMongoFor().ShouldBeEmpty();
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task What_the_index_returns_has_the_shape_of_the_stored_hotel()
    {
        var h = new ReadsHarness().IndexHas(7);

        var served = (await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [7], _ct)).Items.Single();

        var original = ReadsHarness.Hotel(7);
        served.Id.ShouldBe(original.Id);
        served.Code.ShouldBe(original.Code);
        served.Name.ShouldBe(original.Name);
        served.Rating.ShouldBe(original.Rating);
        served.Latitude.ShouldBe(original.Latitude);
        served.Longitude.ShouldBe(original.Longitude);
        served.Images.ShouldBe(original.Images);
        served.Amenities.ShouldBe(original.Amenities);
        served.Rooms.Select(r => (r.Id, r.GroupId, r.Name)).ShouldBe(original.Rooms.Select(r => (r.Id, r.GroupId, r.Name)));
        served.Providers.Select(p => (p.Supplier, p.HotelId)).ShouldBe(original.Providers.Select(p => (p.Supplier, p.HotelId)));
    }

    [Fact]
    public async Task A_full_miss_is_served_from_mongo_and_the_index_is_reported_behind()
    {
        var h = new ReadsHarness().IndexHas().MongoHas(4, 5);

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [4, 5], _ct);

        result.Source.ShouldBe(ContentSource.Mongo);
        Ids(result).ShouldBe([4, 5]);
        var recorded = h.Misses.Recorded.ShouldHaveSingleItem();
        recorded.Supplier.ShouldBe(ReadsHarness.Supplier);
        recorded.Keys.Order().ShouldBe(["4", "5"]);
    }

    [Fact]
    public async Task A_partial_hit_is_completed_from_mongo_asking_only_for_what_is_missing()
    {
        var h = new ReadsHarness().IndexHas(1, 3).MongoHas(2);

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1, 2, 3], _ct);

        result.Source.ShouldBe(ContentSource.Mixed);
        Ids(result).ShouldBe([1, 2, 3]);
        h.AskedMongoFor().ShouldBe([2]);
        h.Misses.Recorded.ShouldHaveSingleItem().Keys.ShouldBe(["2"]);
    }

    [Fact]
    public async Task Ids_that_exist_nowhere_are_absent_and_are_no_signal_that_the_index_is_behind()
    {
        var h = new ReadsHarness().IndexHas(1).MongoHas();

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1, 99], _ct);

        Ids(result).ShouldBe([1]);
        result.Source.ShouldBe(ContentSource.Index, "everything that exists came from the index");
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Requested_ids_are_deduplicated_before_either_store_is_asked()
    {
        var h = new ReadsHarness().IndexHas(1).MongoHas(2);

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [2, 1, 2, 1], _ct);

        Ids(result).ShouldBe([1, 2]);
        h.AskedMongoFor().ShouldBe([2]);
    }

    [Fact]
    public async Task An_unreachable_index_is_served_entirely_from_mongo_and_nothing_is_marked_missing()
    {
        var h = new ReadsHarness().IndexHas(1, 2).MongoHas(1, 2);
        h.Index.FailWhen = operation => operation == "read" ? new SearchIndexUnavailableException("connection refused") : null;

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1, 2], _ct);

        result.Source.ShouldBe(ContentSource.Mongo);
        Ids(result).ShouldBe([1, 2]);
        h.AskedMongoFor().Order().ShouldBe([1, 2]);
        h.Misses.Recorded.ShouldBeEmpty("an outage says nothing about the documents, so no resync is requested");
    }

    [Fact]
    public async Task An_index_that_refuses_the_request_counts_as_not_answering()
    {
        var h = new ReadsHarness().MongoHas(1);
        h.Index.FailWhen = operation => operation == "read" ? new SearchIndexException("index_not_found_exception") : null;

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1], _ct);

        result.Source.ShouldBe(ContentSource.Mongo);
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_slow_index_times_out_and_mongo_serves_without_marking_anything_missing()
    {
        var h = new ReadsHarness().IndexHas(1).MongoHas(1);
        h.Index.BeforeRead = async token =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(Timeout.Infinite, token);
        };

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1], _ct);

        result.Source.ShouldBe(ContentSource.Mongo);
        Ids(result).ShouldBe([1]);
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_that_gives_up_cancels_the_read_instead_of_falling_back()
    {
        var h = new ReadsHarness().IndexHas(1).MongoHas(1);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        h.Index.BeforeRead = async token =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1], caller.Token));

        h.AskedMongoFor().ShouldBeEmpty("a client abort is not a slow index");
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mongo_time_limit_that_expires_surfaces_instead_of_returning_half_an_answer()
    {
        var h = new ReadsHarness().IndexHas(1);
        h.Store.GetByIdsAsync(ReadsHarness.Supplier, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<HotelDocument>>(
                new MongoExecutionTimeoutException(new ConnectionId(new ServerId(new ClusterId(1), new System.Net.DnsEndPoint("localhost", 27017))), "operation exceeded time limit")));

        await Should.ThrowAsync<MongoExecutionTimeoutException>(() => h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1, 2], _ct));

        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_caller_reaches_mongo_as_a_cancelled_token()
    {
        var h = new ReadsHarness().MongoHas(1);
        CancellationToken seen = default;
        h.Store.GetByIdsAsync(ReadsHarness.Supplier, Arg.Any<IReadOnlyCollection<long>>(), Arg.Do<CancellationToken>(t => seen = t))
            .Returns(Task.FromResult<IReadOnlyList<HotelDocument>>([]));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [1], source.Token);
        await source.CancelAsync();

        seen.IsCancellationRequested.ShouldBeTrue("the fallback query carries the caller's token");
    }

    [Fact]
    public async Task An_unknown_supplier_is_refused_before_anything_is_queried()
    {
        var h = new ReadsHarness().IndexHas(1);

        await Should.ThrowAsync<UnknownSupplierException>(() => h.Reader.GetByIdsAsync("supplier-z", [1], _ct));
        await Should.ThrowAsync<UnknownSupplierException>(() => h.Reader.GetByCodesAsync("", ["AB"], _ct));

        h.Index.Reads.ShouldBe(0);
        h.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_for_too_many_keys_is_rejected()
    {
        var h = new ReadsHarness();

        await Should.ThrowAsync<InvalidQueryException>(() => h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [.. Enumerable.Range(1, 11).Select(i => (long)i)], _ct));
    }

    [Fact]
    public async Task An_empty_request_touches_neither_store()
    {
        var h = new ReadsHarness();

        var result = await h.Reader.GetByIdsAsync(ReadsHarness.Supplier, [], _ct);

        result.Items.ShouldBeEmpty();
        h.Index.Reads.ShouldBe(0);
        h.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Codes_follow_the_same_index_first_path_with_completion_from_mongo()
    {
        var h = new ReadsHarness().IndexHas(1);
        h.Store.GetByCodesAsync(ReadsHarness.Supplier, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<HotelDocument>>(
                [.. ((IReadOnlyCollection<string>)call[1]).Where(code => code == "H2").Select(_ => ReadsHarness.Hotel(2))]));

        var result = await h.Reader.GetByCodesAsync(ReadsHarness.Supplier, ["H1", "H2", " ", "H1"], _ct);

        result.Source.ShouldBe(ContentSource.Mixed);
        Ids(result).ShouldBe([1, 2]);
        await h.Store.Received(1).GetByCodesAsync(ReadsHarness.Supplier, Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "H2" })), Arg.Any<CancellationToken>());
        h.Misses.Recorded.ShouldHaveSingleItem().Keys.ShouldBe(["H2"]);
    }

    [Fact]
    public async Task Codes_are_served_from_mongo_when_the_index_is_down()
    {
        var h = new ReadsHarness().IndexHas(1);
        h.Index.FailWhen = operation => operation == "read" ? new SearchIndexUnavailableException("down") : null;
        h.Store.GetByCodesAsync(ReadsHarness.Supplier, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HotelDocument>>([ReadsHarness.Hotel(1)]));

        var result = await h.Reader.GetByCodesAsync(ReadsHarness.Supplier, ["H1"], _ct);

        result.Source.ShouldBe(ContentSource.Mongo);
        h.Misses.Recorded.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ContentSource.Index, "index")]
    [InlineData(ContentSource.Mongo, "mongo")]
    [InlineData(ContentSource.Mixed, "mixed")]
    public void The_source_header_values_are_the_documented_ones(ContentSource source, string expected) =>
        source.ToHeaderValue().ShouldBe(expected);
}
