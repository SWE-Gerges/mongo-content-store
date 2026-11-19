using Content.Mongo.Repository;
using MongoDB.Bson;

namespace Content.Sync.Source;

/// <summary>One keyset page of raw source documents, in ascending <c>_id</c> order.</summary>
public sealed record SourceBatch(IReadOnlyList<BsonDocument> Documents, bool HasMore);

/// <summary>Reads the source collection one keyset page at a time.</summary>
public interface ISourceReader
{
    /// <param name="afterId">Exclusive lower bound on <c>_id</c>, or null to start at the beginning.</param>
    Task<SourceBatch> ReadBatchAsync(string collection, BsonValue? afterId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Reads raw documents so that one document that cannot be decoded can be dead-lettered by its <c>_id</c>
/// without losing the page it arrived in.
/// </summary>
internal sealed class MongoSourceReader(IMongoRepository<BsonDocument> repository) : ISourceReader
{
    public async Task<SourceBatch> ReadBatchAsync(string collection, BsonValue? afterId, int limit, CancellationToken cancellationToken)
    {
        var page = await repository
            .PageByIdAsync(collection, afterId, limit, document => document["_id"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new SourceBatch(page.Items, page.HasMore);
    }
}
