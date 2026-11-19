using MongoDB.Bson;

namespace Content.Sync.Checkpoints;

/// <summary>Where a collection's scan has safely reached.</summary>
/// <param name="Collection">Source collection name (also the checkpoint's key).</param>
/// <param name="LastId">Last <c>_id</c> of the last batch that was fully indexed or dead-lettered.</param>
/// <param name="UpdatedAt">When the checkpoint was written.</param>
/// <param name="RunId">The run that wrote it.</param>
public sealed record SyncCheckpoint(string Collection, BsonValue LastId, DateTimeOffset UpdatedAt, string RunId);

/// <summary>
/// Durable resume points. A checkpoint is written only after every document of its batch is either indexed or
/// durably dead-lettered, and deleted when a run completes.
/// </summary>
public interface ICheckpointStore
{
    Task<SyncCheckpoint?> GetAsync(string collection, CancellationToken cancellationToken);

    Task SaveAsync(string collection, BsonValue lastId, string runId, CancellationToken cancellationToken);

    Task DeleteAsync(string collection, CancellationToken cancellationToken);
}
