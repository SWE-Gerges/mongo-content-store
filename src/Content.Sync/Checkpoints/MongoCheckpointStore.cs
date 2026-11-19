using Content.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Sync.Checkpoints;

/// <summary>Stores checkpoints in <c>_sync_checkpoints</c> as <c>{_id: collection, lastId, updatedAt, runId}</c>.</summary>
internal sealed class MongoCheckpointStore(IMongoDatabaseFactory databases, TimeProvider clock) : ICheckpointStore
{
    public const string CollectionName = "_sync_checkpoints";

    public async Task<SyncCheckpoint?> GetAsync(string collection, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var stored = await Source()
            .Find(Builders<BsonDocument>.Filter.Eq("_id", collection))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored is null
            ? null
            : new SyncCheckpoint(
                collection,
                stored["lastId"],
                new DateTimeOffset(stored["updatedAt"].ToUniversalTime(), TimeSpan.Zero),
                stored["runId"].AsString);
    }

    public async Task SaveAsync(string collection, BsonValue lastId, string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(lastId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var document = new BsonDocument
        {
            { "_id", collection },
            { "lastId", lastId },
            { "updatedAt", clock.GetUtcNow().UtcDateTime },
            { "runId", runId },
        };

        await Source()
            .ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", collection),
                document,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string collection, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        await Source()
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", collection), cancellationToken)
            .ConfigureAwait(false);
    }

    private IMongoCollection<BsonDocument> Source() => databases.Get().GetCollection<BsonDocument>(CollectionName);
}
