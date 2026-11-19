using Content.Mongo;
using Content.Mongo.Repository;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Sync.Source;

/// <summary>What the verification of a new index needs to know about its source collection.</summary>
public interface ISourceStatistics
{
    /// <summary>Number of documents in the collection (a metadata count: cheap, and exact unless the server shut down uncleanly).</summary>
    Task<long> CountAsync(string collection, CancellationToken cancellationToken);

    /// <summary>A random sample of up to <paramref name="size"/> raw documents.</summary>
    Task<IReadOnlyList<BsonDocument>> SampleAsync(string collection, int size, CancellationToken cancellationToken);
}

internal sealed class MongoSourceStatistics(IMongoRepository<BsonDocument> repository, IMongoDatabaseFactory databases) : ISourceStatistics
{
    public Task<long> CountAsync(string collection, CancellationToken cancellationToken) =>
        repository.CountAsync(collection, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<BsonDocument>> SampleAsync(string collection, int size, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        return await databases.Get()
            .GetCollection<BsonDocument>(collection)
            .Aggregate()
            .Sample(size)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
