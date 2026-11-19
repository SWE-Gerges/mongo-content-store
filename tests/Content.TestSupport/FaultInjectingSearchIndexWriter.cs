using Content.Sync.Search;

namespace Content.TestSupport;

/// <summary>
/// Decorator over any <see cref="ISearchIndexWriter"/> for fault injection: throw on a chosen bulk call, or reject
/// chosen documents the way a cluster rejects items inside an otherwise successful bulk response.
/// </summary>
public sealed class FaultInjectingSearchIndexWriter(ISearchIndexWriter inner) : ISearchIndexWriter
{
    private int _bulkCalls;
    private int _ensureCalls;

    /// <summary>Number of bulk requests received so far, including ones that were made to fail.</summary>
    public int BulkCalls => Volatile.Read(ref _bulkCalls);

    /// <summary>Number of requests to create the index so far, including ones that were made to fail.</summary>
    public int EnsureCalls => Volatile.Read(ref _ensureCalls);

    /// <summary>Given the 1-based call number, returns an exception to throw instead of creating the index.</summary>
    public Func<int, Exception?>? EnsureThrowWhen { get; set; }

    /// <summary>Given the 1-based call number and index name, returns an exception to throw instead of indexing.</summary>
    public Func<int, string, Exception?>? ThrowWhen { get; set; }

    /// <summary>Returns a failure to reject a document with (it is not indexed), or null to let it through.</summary>
    public Func<SearchDocument, BulkItemFailure?>? RejectWhen { get; set; }

    public Task EnsureIndexAsync(string indexName, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _ensureCalls);
        return EnsureThrowWhen?.Invoke(call) is { } exception
            ? Task.FromException(exception)
            : inner.EnsureIndexAsync(indexName, cancellationToken);
    }

    public async Task<BulkWriteOutcome> BulkIndexAsync(
        string indexName,
        IReadOnlyList<SearchDocument> documents,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _bulkCalls);
        if (ThrowWhen?.Invoke(call, indexName) is { } exception)
        {
            throw exception;
        }

        var accepted = new List<SearchDocument>(documents.Count);
        var rejected = new List<BulkItemFailure>();
        foreach (var document in documents)
        {
            if (RejectWhen?.Invoke(document) is { } failure)
            {
                rejected.Add(failure);
            }
            else
            {
                accepted.Add(document);
            }
        }

        var outcome = await inner.BulkIndexAsync(indexName, accepted, cancellationToken).ConfigureAwait(false);
        return new BulkWriteOutcome(outcome.Indexed, [.. outcome.Failures, .. rejected]);
    }
}
