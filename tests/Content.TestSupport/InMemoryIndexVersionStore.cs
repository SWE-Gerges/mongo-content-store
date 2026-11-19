using Content.Sync.Indexing;

namespace Content.TestSupport;

/// <summary>Version records in memory.</summary>
public sealed class InMemoryIndexVersionStore : IIndexVersionStore
{
    private readonly Dictionary<string, IndexVersionRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Func<IndexVersionRecord, Exception?>? FailSaveWhen { get; set; }

    public IReadOnlyList<IndexVersionRecord> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _records.Values.OrderBy(r => r.Version)];
            }
        }
    }

    public Task<IReadOnlyList<IndexVersionRecord>> ListAsync(string alias, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IndexVersionRecord>>([.. _records.Values.Where(r => r.Alias == alias).OrderBy(r => r.Version)]);
        }
    }

    public Task SaveAsync(IndexVersionRecord record, CancellationToken cancellationToken)
    {
        if (FailSaveWhen?.Invoke(record) is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _records[record.IndexName] = record;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string indexName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _records.Remove(indexName);
        }

        return Task.CompletedTask;
    }
}
