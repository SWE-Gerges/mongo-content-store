using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Repository;

/// <summary>
/// Generic read/write access to MongoDB. Every method takes the collection name so a single
/// <c>IMongoRepository&lt;T&gt;</c> serves many collections (one per supplier). Every method honours
/// its cancellation token; none of them materialises an unbounded result set unless the caller
/// asks for a filtered list.
/// </summary>
public interface IMongoRepository<T>
{
    Task<T?> GetByIdAsync<TId>(string collection, TId id, string? database = null, CancellationToken cancellationToken = default);

    /// <summary>Fetches by <c>_id</c> with a single <c>$in</c>. Result order is not guaranteed.</summary>
    Task<IReadOnlyList<T>> GetManyAsync<TId>(
        string collection,
        IEnumerable<TId> ids,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> FilterAsync(
        string collection,
        Expression<Func<T, bool>> filter,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> FilterAsync(
        string collection,
        FilterDefinition<T> filter,
        QueryOptions<T>? options = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams matching documents through a server-side cursor, <paramref name="batchSize"/> at a time.
    /// Nothing beyond the current batch is held in memory.
    /// </summary>
    IAsyncEnumerable<T> StreamAsync(
        string collection,
        FilterDefinition<T>? filter = null,
        int batchSize = 1000,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyset page ordered by <c>_id</c> (<c>_id &gt; afterId</c>). Fetches <c>limit + 1</c> documents to
    /// derive <see cref="KeysetPage{T}.HasMore"/> without a count query.
    /// </summary>
    Task<KeysetPage<T>> PageByIdAsync(
        string collection,
        BsonValue? afterId,
        int limit,
        Func<T, BsonValue> idSelector,
        FilterDefinition<T>? filter = null,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts matching documents. With no filter the collection metadata count is used (fast, an estimate
    /// after an unclean shutdown); with a filter an exact count is computed, bounded by <paramref name="maxTime"/>.
    /// </summary>
    Task<long> CountAsync(
        string collection,
        FilterDefinition<T>? filter = null,
        TimeSpan? maxTime = null,
        string? database = null,
        CancellationToken cancellationToken = default);

    /// <summary>Unordered bulk replace-with-upsert keyed by <c>_id</c>; re-running overwrites instead of duplicating.</summary>
    Task<BulkUpsertResult> UpsertManyAsync(
        string collection,
        IReadOnlyCollection<T> documents,
        Func<T, BsonValue> idSelector,
        string? database = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync<TId>(string collection, TId id, string? database = null, CancellationToken cancellationToken = default);
}
