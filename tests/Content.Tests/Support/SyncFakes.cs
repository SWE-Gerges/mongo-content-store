using System.Collections.Concurrent;
using Content.Sync.Checkpoints;
using Content.Sync.DeadLetters;
using Content.Sync.Search;
using Content.Sync.Source;
using MongoDB.Bson;

namespace Content.Tests.Support;

/// <summary>Ordered, thread-safe record of side effects, so a test can assert what happened before what.</summary>
internal sealed class OperationLog
{
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();

    public void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }
}

internal sealed class InMemorySource : ISourceReader, ISourceStatistics
{
    private readonly Dictionary<string, List<BsonDocument>> _collections = new(StringComparer.Ordinal);
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Called before each read with the 1-based read number; may throw or block.</summary>
    public Func<string, int, CancellationToken, Task>? BeforeRead { get; set; }

    public InMemorySource Add(string collection, IEnumerable<BsonDocument> documents)
    {
        _collections[collection] = [.. documents.OrderBy(d => d["_id"])];
        return this;
    }

    public static IEnumerable<BsonDocument> Hotels(IEnumerable<long> ids) =>
        ids.Select(id => new BsonDocument { { "_id", id }, { "Name", $"Hotel {id}" }, { "CityCode", "C001" }, { "CountryCode", "XX" }, { "Rating", 4 } });

    public IReadOnlyList<string> Collections => [.. _collections.Keys];

    public Task<long> CountAsync(string collection, CancellationToken cancellationToken) =>
        Task.FromResult((long)_collections[collection].Count);

    /// <summary>Evenly spaced documents, so a test knows which ones are checked.</summary>
    public Task<IReadOnlyList<BsonDocument>> SampleAsync(string collection, int size, CancellationToken cancellationToken)
    {
        var all = _collections[collection];
        var step = Math.Max(1, all.Count / Math.Max(1, size));
        return Task.FromResult<IReadOnlyList<BsonDocument>>([.. all.Where((_, index) => index % step == 0).Take(size)]);
    }

    public async Task<SourceBatch> ReadBatchAsync(string collection, BsonValue? afterId, int limit, CancellationToken cancellationToken)
    {
        var read = Interlocked.Increment(ref _reads);
        if (BeforeRead is not null)
        {
            await BeforeRead(collection, read, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var remaining = _collections[collection].Where(d => afterId is null || d["_id"].CompareTo(afterId) > 0).Take(limit + 1).ToList();
        var hasMore = remaining.Count > limit;
        return new SourceBatch(hasMore ? remaining[..limit] : remaining, hasMore);
    }
}

internal sealed class InMemoryCheckpoints(OperationLog? log = null) : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, SyncCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    public Func<string, BsonValue, Exception?>? FailSaveWhen { get; set; }

    public IReadOnlyList<BsonValue> Saved { get; private set; } = [];

    public Task<SyncCheckpoint?> GetAsync(string collection, CancellationToken cancellationToken) =>
        Task.FromResult(_checkpoints.TryGetValue(collection, out var checkpoint) ? checkpoint : null);

    public Task SaveAsync(string collection, BsonValue lastId, string runId, CancellationToken cancellationToken)
    {
        if (FailSaveWhen?.Invoke(collection, lastId) is { } failure)
        {
            throw failure;
        }

        _checkpoints[collection] = new SyncCheckpoint(collection, lastId, DateTimeOffset.UnixEpoch, runId);
        Saved = [.. Saved, lastId];
        log?.Add($"checkpoint:{collection}:{lastId}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string collection, CancellationToken cancellationToken)
    {
        _checkpoints.TryRemove(collection, out _);
        log?.Add($"checkpoint-deleted:{collection}");
        return Task.CompletedTask;
    }

    public BsonValue? Current(string collection) => _checkpoints.TryGetValue(collection, out var c) ? c.LastId : null;
}

/// <summary>
/// Dead-letter store with the semantics of the Mongo one: one record per collection and document, a repeat bumps
/// <c>Attempts</c> and keeps <c>FirstSeenAt</c>, listings are newest first with ties by id and keyset paging.
/// </summary>
internal sealed class InMemoryDeadLetters(OperationLog? log = null, TimeProvider? clock = null) : IDeadLetterStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Stored> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record Stored(string Collection, string? Scope, DeadLetterEntry Entry, DateTimeOffset FirstSeenAt, int Attempts);

    public Func<IReadOnlyCollection<DeadLetterEntry>, Exception?>? FailWriteWhen { get; set; }

    public IReadOnlyDictionary<string, DeadLetterEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToDictionary(e => e.Key, e => e.Value.Entry);
            }
        }
    }

    public Task WriteAsync(string collection, IReadOnlyCollection<DeadLetterEntry> entries, string? scope, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (FailWriteWhen?.Invoke(entries) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            var now = TruncateToMilliseconds(_clock.GetUtcNow());
            foreach (var entry in entries)
            {
                var key = $"{collection}|{entry.DocId}";
                _entries[key] = _entries.TryGetValue(key, out var existing)
                    ? existing with { Entry = entry, Scope = scope, Attempts = existing.Attempts + 1 }
                    : new Stored(collection, scope, entry, now, 1);
            }
        }

        log?.Add($"deadletter:{collection}:{string.Join(",", entries.Select(e => e.DocId).Order(StringComparer.Ordinal))}");
        return Task.CompletedTask;
    }

    public Task<DeadLetterPage> ListAsync(string? collection, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        List<(string Key, Stored Value)> ordered;
        lock (_gate)
        {
            ordered = _entries
                .Where(e => string.IsNullOrWhiteSpace(collection) || e.Value.Collection == collection)
                .Select(e => (e.Key, e.Value))
                .OrderByDescending(e => e.Value.FirstSeenAt)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .ToList();
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            var (milliseconds, id) = MongoDeadLetterStore.DecodeCursor(cursor);
            var seen = DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
            ordered = ordered
                .Where(e => e.Value.FirstSeenAt < seen || (e.Value.FirstSeenAt == seen && string.CompareOrdinal(e.Key, id) > 0))
                .ToList();
        }

        var hasMore = ordered.Count > pageSize;
        var page = ordered.Take(pageSize).ToList();
        var items = page
            .Select(e => new DeadLetterRecord(e.Value.Collection, e.Value.Entry.DocId, e.Value.Entry.Reason, e.Value.FirstSeenAt, e.Value.Attempts, e.Value.Entry.PayloadHash))
            .ToList();
        var next = hasMore ? MongoDeadLetterStore.EncodeCursor(page[^1].Value.FirstSeenAt.ToUnixTimeMilliseconds(), page[^1].Key) : null;
        return Task.FromResult(new DeadLetterPage(items, next));
    }

    public Task<long> CountAsync(string collection, string? scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_entries.Values.Count(e => e.Collection == collection && (scope is null || e.Scope == scope)));
        }
    }

    public Task RemoveAsync(string collection, IReadOnlyCollection<string> docIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var id in docIds)
            {
                _entries.Remove($"{collection}|{id}");
            }
        }

        return Task.CompletedTask;
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}
