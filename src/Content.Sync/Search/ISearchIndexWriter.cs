namespace Content.Sync.Search;

/// <summary>One document a bulk call rejected even though the call itself succeeded.</summary>
/// <param name="Id">Search document id.</param>
/// <param name="Status">HTTP-style status reported for the item.</param>
/// <param name="Reason">Error type and reason from the cluster.</param>
/// <param name="Retryable">True for transient rejections (429/503): worth one more attempt.</param>
public sealed record BulkItemFailure(string Id, int Status, string Reason, bool Retryable);

/// <summary>Result of one bulk request: how many documents were indexed and which were rejected.</summary>
public sealed record BulkWriteOutcome(int Indexed, IReadOnlyList<BulkItemFailure> Failures)
{
    public static BulkWriteOutcome AllIndexed(int count) => new(count, []);
}

/// <summary>The search cluster could not be reached or answered with a transient failure.</summary>
public class SearchIndexUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The search cluster refused a request in a way retrying will not fix (bad request, forbidden ...).</summary>
public class SearchIndexException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Items stayed rejected as retryable after their one retry: the cluster is overloaded, not the data bad.</summary>
public sealed class BulkRetriesExhaustedException(int count, string sampleIds)
    : SearchIndexUnavailableException($"{count} document(s) were still rejected as retryable after a retry (e.g. {sampleIds}).");

/// <summary>
/// The only seam through which sync writes to the search index. Tests wrap it with a decorator that
/// throws on the Nth bulk call or rejects chosen ids.
/// </summary>
public interface ISearchIndexWriter
{
    /// <summary>Creates the index with its explicit mapping if it does not exist. Idempotent.</summary>
    Task EnsureIndexAsync(string indexName, CancellationToken cancellationToken);

    /// <summary>
    /// Indexes the documents (id = <see cref="SearchDocument.Id"/>, so a repeat overwrites). Throws
    /// <see cref="SearchIndexUnavailableException"/> for transient request-level failures and
    /// <see cref="SearchIndexException"/> for permanent ones; per-document rejections come back in the outcome.
    /// </summary>
    Task<BulkWriteOutcome> BulkIndexAsync(string indexName, IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken);
}
