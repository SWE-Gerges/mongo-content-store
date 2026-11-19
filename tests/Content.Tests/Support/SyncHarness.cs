using Content.Sync;
using Content.Sync.Configuration;
using Content.Sync.Search;
using Content.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace Content.Tests.Support;

/// <summary>A time provider that records every timer it is asked to create, so tests can assert on backoff delays.</summary>
internal sealed class RecordingTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    private readonly List<TimeSpan> _dueTimes = [];
    private readonly Lock _gate = new();

    public FakeTimeProvider Inner { get; } = inner;

    public IReadOnlyList<TimeSpan> DueTimes
    {
        get
        {
            lock (_gate)
            {
                return [.. _dueTimes];
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();

    public override long GetTimestamp() => Inner.GetTimestamp();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _dueTimes.Add(dueTime);
        }

        return Inner.CreateTimer(callback, state, dueTime, period);
    }
}

/// <summary>Everything a sync test needs, wired over in-memory fakes with all delays set to zero.</summary>
internal sealed class SyncHarness
{
    public const string Collection = "hotels";

    public SyncHarness()
    {
        Clock = new RecordingTimeProvider(new FakeTimeProvider());
        Writer = new FaultInjectingSearchIndexWriter(Index);
        Checkpoints = new InMemoryCheckpoints(Log);
        DeadLetters = new InMemoryDeadLetters(Log);
        Runner = new SyncRunner(
            Source,
            new OpenSearchBulkIndexer(Writer, Clock, NullLogger<OpenSearchBulkIndexer>.Instance),
            Writer,
            Checkpoints,
            DeadLetters,
            new SearchIndexNaming(Options.Create(new SearchOptions { Uri = "http://localhost:9200" })),
            Clock,
            NullLogger<SyncRunner>.Instance);
    }

    public InMemorySource Source { get; } = new();

    public InMemorySearchCluster Index { get; } = new();

    public OperationLog Log { get; } = new();

    public RecordingTimeProvider Clock { get; }

    public FaultInjectingSearchIndexWriter Writer { get; }

    public InMemoryCheckpoints Checkpoints { get; }

    public InMemoryDeadLetters DeadLetters { get; }

    public SyncRunner Runner { get; }

    public string IndexName(string collection = Collection) => "content-" + collection;

    public SyncOptions Fast(int batchSize = 100) => new()
    {
        BatchSize = batchSize,
        BulkChunkSize = 100,
        BulkMaxParallelism = 2,
        MaxReadAttempts = 3,
        ReadBackoffBase = TimeSpan.Zero,
        MaxBulkAttempts = 3,
        BulkBackoffBase = TimeSpan.Zero,
        ItemRetryDelay = TimeSpan.Zero,
    };

    public SyncHarness WithHotels(int count, string collection = Collection)
    {
        Source.Add(collection, InMemorySource.Hotels(Enumerable.Range(1, count).Select(i => (long)i)));
        return this;
    }

    public static string[] IdStrings(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => i.ToString()).ToArray();

    public static BsonValue Id(long id) => new BsonInt64(id);
}
