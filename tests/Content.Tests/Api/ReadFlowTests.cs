using System.Net;
using System.Text.Json;
using Content.Store.Models;
using Content.Sync.Search;
using Content.Tests.Support;
using NSubstitute;

namespace Content.Tests.Api;

/// <summary>The content routes over the real read path, with the search index and the content store as separate fakes.</summary>
[Collection(ClassMapCollection.Name)]
public sealed class ReadFlowTests : IDisposable
{
    private const string Alias = "content-hotel_supplier-a";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeWorld _world = new();
    private readonly FakeBackedApiFactory _host;
    private readonly HttpClient _client;

    public ReadFlowTests()
    {
        _ = TestMappings.Counter;
        _host = new FakeBackedApiFactory(_world);
        _client = _host.CreateClientWithKey(FakeBackedApiFactory.ReaderKey);
        _world.Index.Seed(Alias + "-v1", [], Alias);
        _host.Store.GetByIdsAsync("supplier-a", Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<HotelDocument>>(
                [.. ((IReadOnlyCollection<long>)call[1]).Where(id => id is >= 100 and < 200).Select(id => ReadsHarness.Hotel(id))]));
        _host.Store.GetByCodesAsync("supplier-a", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<HotelDocument>>(
                [.. ((IReadOnlyCollection<string>)call[1]).Where(code => code == "H150").Select(_ => ReadsHarness.Hotel(150))]));
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    private void IndexHas(params long[] ids) =>
        _world.Index.Seed(Alias + "-v1", ids.Select(id => HotelSearchDocumentMapper.Map(ReadsHarness.Hotel(id))), Alias);

    private async Task<(string? Source, JsonElement Body)> GetAsync(string url)
    {
        var response = await _client.GetAsync(url, _ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
        var source = response.Headers.TryGetValues("X-Content-Source", out var values) ? values.Single() : null;
        return (source, JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement.Clone());
    }

    [Fact]
    public async Task Ids_the_index_holds_are_served_from_it_with_the_index_header()
    {
        IndexHas(1, 2);

        var (source, body) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=2,1");

        source.ShouldBe("index");
        body.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("id").GetInt64()).ShouldBe([1L, 2L]);
        _host.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ids_missing_from_the_index_are_completed_from_mongo_and_the_header_says_so()
    {
        IndexHas(1);

        var (mixed, mixedBody) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=1,150");
        var (mongo, _) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=150,250");

        mixed.ShouldBe("mixed");
        mixedBody.GetProperty("items").GetArrayLength().ShouldBe(2);
        mongo.ShouldBe("mongo");
        _world.Misses.Recorded.Select(m => string.Join(",", m.Keys)).ShouldBe(["150", "150"]);
    }

    [Fact]
    public async Task The_same_hotel_has_the_same_response_body_from_the_index_and_from_mongo()
    {
        var (fromMongo, mongoBody) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=150");
        IndexHas(150);

        var (fromIndex, indexBody) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=150");

        fromMongo.ShouldBe("mongo");
        fromIndex.ShouldBe("index");
        indexBody.GetRawText().ShouldBe(mongoBody.GetRawText(), "the documented response shape does not depend on where the content came from");
    }

    [Fact]
    public async Task An_index_outage_is_served_from_mongo_and_no_resync_signal_is_recorded()
    {
        IndexHas(1);
        _world.Index.FailWhen = operation => operation == "read" ? new SearchIndexUnavailableException("connection refused") : null;

        var (source, body) = await GetAsync("/api/v1/content/supplier-a/hotels?ids=150");

        source.ShouldBe("mongo");
        body.GetProperty("items").GetArrayLength().ShouldBe(1);
        _world.Misses.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hotel_by_code_comes_from_the_index_when_it_has_it_and_from_mongo_otherwise()
    {
        IndexHas(1);

        var (viaIndex, _) = await GetAsync("/api/v1/content/supplier-a/hotels/by-code/H1");
        var (viaMongo, _) = await GetAsync("/api/v1/content/supplier-a/hotels/by-code/H150");
        var missing = await _client.GetAsync("/api/v1/content/supplier-a/hotels/by-code/NOPE", _ct);

        viaIndex.ShouldBe("index");
        viaMongo.ShouldBe("mongo");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Routes_that_the_index_does_not_serve_are_labelled_mongo()
    {
        _host.Store.GetByCityAsync("supplier-a", "C001", Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new Content.Store.CityPage([1, 2], null));

        var (source, _) = await GetAsync("/api/v1/content/supplier-a/hotels?cityCode=C001");

        source.ShouldBe("mongo");
    }

    [Fact]
    public async Task An_unknown_supplier_is_a_404_problem_and_never_reaches_either_store()
    {
        var response = await _client.GetAsync("/api/v1/content/supplier-z/hotels?ids=1", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        _world.Index.Reads.ShouldBe(0);
        _host.Store.ReceivedCalls().ShouldBeEmpty();
    }
}
