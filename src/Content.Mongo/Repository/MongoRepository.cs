using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Repository;

internal sealed class MongoRepository<T>(IMongoDatabaseFactory databases) : IMongoRepository<T>
{
    private static FilterDefinitionBuilder<T> Filter => Builders<T>.Filter;

    public async Task<T?> GetByIdAsync<TId>(string collection, TId id, string? database = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var source = Collection(collection, database);
        return await source.Find(Filter.Eq("_id", id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> GetManyAsync<TId>(
        string collection,
        IEnumerable<TId> ids,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var source = Collection(collection, database);
        var idList = ids as IReadOnlyCollection<TId> ?? ids.ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var find = source.Find(Filter.In("_id", idList));
        return await Project(find, projection).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<T>> FilterAsync(
        string collection,
        Expression<Func<T, bool>> filter,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return FilterAsync(
            collection,
            Filter.Where(filter),
            new QueryOptions<T> { Projection = projection },
            database,
            cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FilterAsync(
        string collection,
        FilterDefinition<T> filter,
        QueryOptions<T>? options = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var source = Collection(collection, database);
        var find = source.Find(filter, new FindOptions { Collation = options?.Collation, MaxTime = options?.MaxTime });
        if (options?.Sort is { } sort)
        {
            find = find.Sort(sort);
        }

        if (options?.Limit is { } limit)
        {
            find = find.Limit(limit);
        }

        return await Project(find, options?.Projection).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<T> StreamAsync(
        string collection,
        FilterDefinition<T>? filter = null,
        int batchSize = 1000,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var source = Collection(collection, database);
        return StreamCore(source, filter ?? Filter.Empty, batchSize, projection, cancellationToken);
    }

    public async Task<KeysetPage<T>> PageByIdAsync(
        string collection,
        BsonValue? afterId,
        int limit,
        Func<T, BsonValue> idSelector,
        FilterDefinition<T>? filter = null,
        ProjectionDefinition<T>? projection = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentNullException.ThrowIfNull(idSelector);
        var source = Collection(collection, database);

        var combined = filter ?? Filter.Empty;
        if (afterId is not null)
        {
            combined = Filter.And(combined, Filter.Gt("_id", afterId));
        }

        var find = source.Find(combined).Sort(Builders<T>.Sort.Ascending("_id")).Limit(checked(limit + 1));
        var fetched = await Project(find, projection).ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = fetched.Count > limit;
        if (hasMore)
        {
            fetched.RemoveAt(fetched.Count - 1);
        }

        BsonValue? lastId = fetched.Count == 0 ? null : idSelector(fetched[^1]);
        return new KeysetPage<T>(fetched, hasMore, lastId);
    }

    public async Task<long> CountAsync(
        string collection,
        FilterDefinition<T>? filter = null,
        TimeSpan? maxTime = null,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var source = Collection(collection, database);
        if (filter is null)
        {
            return await source
                .EstimatedDocumentCountAsync(new EstimatedDocumentCountOptions { MaxTime = maxTime }, cancellationToken)
                .ConfigureAwait(false);
        }

        return await source
            .CountDocumentsAsync(filter, new CountOptions { MaxTime = maxTime }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BulkUpsertResult> UpsertManyAsync(
        string collection,
        IReadOnlyCollection<T> documents,
        Func<T, BsonValue> idSelector,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(idSelector);
        var source = Collection(collection, database);
        if (documents.Count == 0)
        {
            return BulkUpsertResult.Empty;
        }

        var models = documents
            .Select(document => new ReplaceOneModel<T>(Filter.Eq("_id", idSelector(document)), document) { IsUpsert = true })
            .ToList();

        var result = await source
            .BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);
        return new BulkUpsertResult(result.MatchedCount, result.ModifiedCount, result.Upserts.Count);
    }

    public async Task<bool> DeleteAsync<TId>(string collection, TId id, string? database = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var source = Collection(collection, database);
        var result = await source.DeleteOneAsync(Filter.Eq("_id", id), cancellationToken).ConfigureAwait(false);
        return result.DeletedCount > 0;
    }

    private static async IAsyncEnumerable<T> StreamCore(
        IMongoCollection<T> source,
        FilterDefinition<T> filter,
        int batchSize,
        ProjectionDefinition<T>? projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var find = Project(source.Find(filter, new FindOptions { BatchSize = batchSize }), projection);
        using var cursor = await find.ToCursorAsync(cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return document;
            }
        }
    }

    private static IFindFluent<T, T> Project(IFindFluent<T, T> find, ProjectionDefinition<T>? projection) =>
        projection is null ? find : find.Project<T>(projection);

    private IMongoCollection<T> Collection(string collection, string? database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        return databases.Get(database).GetCollection<T>(collection);
    }
}
