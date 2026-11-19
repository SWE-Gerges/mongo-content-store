using Content.Sync.Configuration;
using Content.Sync.Search;
using Content.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Content.Tests.Sync;

public class BulkIndexerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static IReadOnlyList<SearchDocument> Documents(int count) =>
        Enumerable.Range(1, count).Select(i => new SearchDocument { Id = i.ToString(), Name = $"Hotel {i}" }).ToList();

    private static SyncOptions Options(int chunk = 500, int parallelism = 2, int attempts = 3) => new()
    {
        BulkChunkSize = chunk,
        BulkMaxParallelism = parallelism,
        MaxBulkAttempts = attempts,
        BulkBackoffBase = TimeSpan.Zero,
        ItemRetryDelay = TimeSpan.Zero,
    };

    private static OpenSearchBulkIndexer Indexer(ISearchIndexWriter writer) =>
        new(writer, TimeProvider.System, NullLogger<OpenSearchBulkIndexer>.Instance);

    private sealed class ScriptedWriter(Func<int, IReadOnlyList<SearchDocument>, CancellationToken, Task<BulkWriteOutcome>> bulk) : ISearchIndexWriter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task EnsureIndexAsync(string indexName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BulkWriteOutcome> BulkIndexAsync(string indexName, IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken) =>
            bulk(Interlocked.Increment(ref _calls), documents, cancellationToken);
    }

    [Fact]
    public async Task Splits_a_batch_into_chunks_of_the_configured_size()
    {
        var sizes = new List<int>();
        var writer = new ScriptedWriter((_, docs, _) =>
        {
            lock (sizes)
            {
                sizes.Add(docs.Count);
            }

            return Task.FromResult(BulkWriteOutcome.AllIndexed(docs.Count));
        });

        var result = await Indexer(writer).IndexAsync("idx", Documents(1200), Options(), _ct);

        result.Indexed.ShouldBe(1200);
        result.Rejected.ShouldBeEmpty();
        sizes.Order().ShouldBe([200, 500, 500]);
    }

    [Fact]
    public async Task Never_has_more_chunks_in_flight_than_the_configured_parallelism_and_does_use_it()
    {
        var inFlight = 0;
        var peak = 0;
        var twoInFlight = new TaskCompletionSource();
        var writer = new ScriptedWriter(async (_, docs, token) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            if (now == 2)
            {
                twoInFlight.TrySetResult();
            }

            await twoInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref inFlight);
            return BulkWriteOutcome.AllIndexed(docs.Count);
        });

        await Indexer(writer).IndexAsync("idx", Documents(2000), Options(chunk: 500, parallelism: 2), _ct);

        peak.ShouldBe(2);
        writer.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task Permanent_rejections_are_returned_with_their_reasons_and_the_rest_is_counted_as_indexed()
    {
        var writer = new ScriptedWriter((_, docs, _) =>
        {
            var failures = docs.Where(d => d.Id is "3" or "700")
                .Select(d => new BulkItemFailure(d.Id, 400, "mapper_parsing_exception: bad value", Retryable: false))
                .ToList();
            return Task.FromResult(new BulkWriteOutcome(docs.Count - failures.Count, failures));
        });

        var result = await Indexer(writer).IndexAsync("idx", Documents(1000), Options(), _ct);

        result.Indexed.ShouldBe(998);
        result.Rejected.Select(r => r.Id).Order().ShouldBe(["3", "700"]);
        result.Rejected.ShouldAllBe(r => r.Reason.Contains("status 400") && r.Reason.Contains("mapper_parsing_exception"));
        writer.Calls.ShouldBe(2, "permanent rejections are not retried");
    }

    [Fact]
    public async Task Retries_only_the_retryable_items_and_only_once()
    {
        var sent = new List<string[]>();
        var writer = new ScriptedWriter((call, docs, _) =>
        {
            lock (sent)
            {
                sent.Add([.. docs.Select(d => d.Id)]);
            }

            return Task.FromResult(call == 1
                ? new BulkWriteOutcome(docs.Count - 2, [new BulkItemFailure("2", 429, "es_rejected_execution_exception", true), new BulkItemFailure("4", 400, "mapper_parsing_exception", false)])
                : BulkWriteOutcome.AllIndexed(docs.Count));
        });

        var result = await Indexer(writer).IndexAsync("idx", Documents(10), Options(), _ct);

        sent.Count.ShouldBe(2);
        sent[1].ShouldBe(["2"]);
        result.Indexed.ShouldBe(9);
        result.Rejected.Select(r => r.Id).ShouldBe(["4"]);
    }

    [Fact]
    public async Task Items_still_retryable_after_the_retry_raise_instead_of_being_dropped()
    {
        var writer = new ScriptedWriter((_, docs, _) => Task.FromResult(new BulkWriteOutcome(
            docs.Count - 1,
            [new BulkItemFailure("1", 503, "unavailable", true)])));

        var failure = await Should.ThrowAsync<BulkRetriesExhaustedException>(() => Indexer(writer).IndexAsync("idx", Documents(5), Options(), _ct));

        failure.Message.ShouldContain("1");
        writer.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_request_level_outage_is_retried_with_backoff_then_surfaced()
    {
        var writer = new ScriptedWriter((_, _, _) => Task.FromException<BulkWriteOutcome>(new SearchIndexUnavailableException("down")));

        await Should.ThrowAsync<SearchIndexUnavailableException>(() => Indexer(writer).IndexAsync("idx", Documents(5), Options(attempts: 4), _ct));

        writer.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task A_single_failing_chunk_surfaces_its_own_exception_and_stops_the_others()
    {
        var writer = new ScriptedWriter(async (call, docs, token) =>
        {
            if (call == 1)
            {
                throw new SearchIndexException("mapping is broken");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return BulkWriteOutcome.AllIndexed(docs.Count);
        });

        var failure = await Should.ThrowAsync<SearchIndexException>(() => Indexer(writer).IndexAsync("idx", Documents(1000), Options(chunk: 500), _ct));

        failure.Message.ShouldBe("mapping is broken");
    }

    [Fact]
    public async Task Failures_in_several_parallel_chunks_are_aggregated_not_swallowed()
    {
        // Both chunks must be past every awaited step before either throws, otherwise the first failure
        // could cancel the second before it has a failure of its own to report.
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var resumed = 0;
        var writer = new ScriptedWriter(async (call, _, _) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (Interlocked.Increment(ref resumed) == 2)
            {
                bothResumed.TrySetResult();
            }

            await bothResumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            throw call == 1 ? new SearchIndexException("first chunk") : new SearchIndexException("second chunk");
        });

        var failure = await Should.ThrowAsync<AggregateException>(() => Indexer(writer).IndexAsync("idx", Documents(1000), Options(chunk: 500, attempts: 1), _ct));

        failure.InnerExceptions.Select(e => e.Message).Order().ShouldBe(["first chunk", "second chunk"]);
    }

    [Fact]
    public async Task Cancellation_propagates_as_cancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var writer = new ScriptedWriter(async (_, docs, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return BulkWriteOutcome.AllIndexed(docs.Count);
        });

        (await Record.ExceptionAsync(() => Indexer(writer).IndexAsync("idx", Documents(10), Options(), cts.Token)))
            .ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task An_empty_batch_makes_no_request()
    {
        var writer = new ScriptedWriter((_, docs, _) => Task.FromResult(BulkWriteOutcome.AllIndexed(docs.Count)));

        var result = await Indexer(writer).IndexAsync("idx", [], Options(), _ct);

        result.Indexed.ShouldBe(0);
        writer.Calls.ShouldBe(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

public class SearchDocumentIdTests
{
    [Fact]
    public void Source_ids_render_distinctly_whatever_their_formatting()
    {
        BsonValueIds().Select(id => HotelSearchDocumentMapper.RenderId(id)).Distinct().Count().ShouldBe(BsonValueIds().Length);
    }

    [Fact]
    public void Renders_integers_without_a_floating_point_detour()
    {
        HotelSearchDocumentMapper.RenderId(new MongoDB.Bson.BsonInt64(9_007_199_254_740_993)).ShouldBe("9007199254740993");
        HotelSearchDocumentMapper.RenderId(new MongoDB.Bson.BsonInt32(-5)).ShouldBe("-5");
    }

    [Fact]
    public void Renders_strings_verbatim_so_padding_and_case_stay_significant()
    {
        HotelSearchDocumentMapper.RenderId(new MongoDB.Bson.BsonString("007")).ShouldBe("007");
        HotelSearchDocumentMapper.RenderId(new MongoDB.Bson.BsonString("A-1")).ShouldBe("A-1");
    }

    private static MongoDB.Bson.BsonValue[] BsonValueIds() =>
    [
        new MongoDB.Bson.BsonString("7"),
        new MongoDB.Bson.BsonString("07"),
        new MongoDB.Bson.BsonString("007"),
        new MongoDB.Bson.BsonString("7 "),
        new MongoDB.Bson.BsonString(" 7"),
        new MongoDB.Bson.BsonString("7.0"),
        new MongoDB.Bson.BsonString("a-1"),
        new MongoDB.Bson.BsonString("A-1"),
        new MongoDB.Bson.BsonInt64(8),
        new MongoDB.Bson.BsonInt64(9_007_199_254_740_992),
        new MongoDB.Bson.BsonInt64(9_007_199_254_740_993),
        new MongoDB.Bson.BsonObjectId(MongoDB.Bson.ObjectId.Parse("507f1f77bcf86cd799439011")),
        new MongoDB.Bson.BsonObjectId(MongoDB.Bson.ObjectId.Parse("507f1f77bcf86cd799439012")),
    ];
}
