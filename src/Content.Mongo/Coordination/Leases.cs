using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Coordination;

/// <summary>A held lease. Identified by key, owner and run so a stale holder cannot renew someone else's lease.</summary>
public sealed record Lease(string Key, string OwnerId, string RunId);

public sealed record LeaseInfo(string Key, string OwnerId, string RunId, DateTimeOffset HeartbeatAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Mutual exclusion per key (a collection, a migration) across processes. A lease is taken atomically, kept alive
/// by heartbeats and replaced once it has expired, so a crashed owner never blocks the key for longer than the lease.
/// </summary>
public interface ILeaseStore
{
    /// <returns>The lease, or null when another owner holds an unexpired one.</returns>
    Task<Lease?> TryAcquireAsync(string key, string ownerId, string runId, TimeSpan duration, CancellationToken cancellationToken);

    /// <returns>False when the lease is no longer held by this owner and run (someone else took it over).</returns>
    Task<bool> RenewAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken);

    Task ReleaseAsync(Lease lease, CancellationToken cancellationToken);

    Task<LeaseInfo?> GetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Lease store over the <c>sync_leases</c> collection. Acquisition is one <c>findOneAndUpdate</c> with upsert whose
/// filter only matches an expired lease: an absent lease is inserted, an expired one is taken over, and a live one
/// makes the upsert collide on <c>_id</c> (duplicate key), which is how losing the race is detected. Expiry is judged
/// by the callers' clocks, so instances are expected to keep their clocks roughly in step (well inside the lease duration).
/// </summary>
internal sealed class MongoLeaseStore(IMongoDatabaseFactory databases, TimeProvider clock) : ILeaseStore
{
    public const string CollectionName = "sync_leases";
    private const int DuplicateKey = 11000;

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    private static UpdateDefinitionBuilder<BsonDocument> Update => Builders<BsonDocument>.Update;

    public async Task<Lease?> TryAcquireAsync(string key, string ownerId, string runId, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = clock.GetUtcNow();
        var expired = Filter.And(Filter.Eq("_id", key), Filter.Lt("expiresAt", now.UtcDateTime));
        var take = Update
            .Set("ownerId", ownerId)
            .Set("runId", runId)
            .Set("acquiredAt", now.UtcDateTime)
            .Set("heartbeatAt", now.UtcDateTime)
            .Set("expiresAt", (now + duration).UtcDateTime);

        try
        {
            await Source()
                .FindOneAndUpdateAsync(expired, take, new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);
            return new Lease(key, ownerId, runId);
        }
        catch (MongoCommandException ex) when (ex.Code == DuplicateKey)
        {
            return null;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    public async Task<bool> RenewAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Still being the recorded owner is enough, even if the lease lapsed meanwhile and nobody took it over.
        var result = await Source()
            .UpdateOneAsync(
                Filter.And(Filter.Eq("_id", lease.Key), Filter.Eq("ownerId", lease.OwnerId), Filter.Eq("runId", lease.RunId)),
                Update.Set("heartbeatAt", now.UtcDateTime).Set("expiresAt", (now + duration).UtcDateTime),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    public async Task ReleaseAsync(Lease lease, CancellationToken cancellationToken) =>
        await Source()
            .DeleteOneAsync(
                Filter.And(Filter.Eq("_id", lease.Key), Filter.Eq("ownerId", lease.OwnerId), Filter.Eq("runId", lease.RunId)),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<LeaseInfo?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await Source().Find(Filter.Eq("_id", key)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return stored is null
            ? null
            : new LeaseInfo(
                key,
                stored["ownerId"].AsString,
                stored["runId"].AsString,
                new DateTimeOffset(stored["heartbeatAt"].ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(stored["expiresAt"].ToUniversalTime(), TimeSpan.Zero));
    }

    private IMongoCollection<BsonDocument> Source() => databases.Get().GetCollection<BsonDocument>(CollectionName);
}
