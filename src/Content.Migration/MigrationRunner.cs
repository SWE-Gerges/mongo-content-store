using System.Globalization;
using Content.Mongo.Repository;
using Content.Sync.Checkpoints;
using Content.Sync.DeadLetters;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Migration;

/// <summary>Where migrated documents are written and counted.</summary>
public interface IMigrationTarget
{
    /// <summary>Unordered upsert by <c>_id</c>: writing the same documents again replaces them instead of duplicating.</summary>
    Task UpsertAsync(string collection, IReadOnlyList<BsonDocument> documents, CancellationToken cancellationToken);

    /// <summary>Exact number of documents in the collection.</summary>
    Task<long> CountAsync(string collection, CancellationToken cancellationToken);
}

internal sealed class MongoMigrationTarget(IMongoRepository<BsonDocument> repository) : IMigrationTarget
{
    public async Task UpsertAsync(string collection, IReadOnlyList<BsonDocument> documents, CancellationToken cancellationToken) =>
        await repository.UpsertManyAsync(collection, documents, document => document["_id"], cancellationToken: cancellationToken).ConfigureAwait(false);

    public Task<long> CountAsync(string collection, CancellationToken cancellationToken) =>
        repository.CountAsync(collection, Builders<BsonDocument>.Filter.Empty, cancellationToken: cancellationToken);
}

/// <summary>Source rows against target documents, with the dead letters that explain the difference.</summary>
/// <param name="ExpectedTarget">What the target should hold: the source count minus the rows that are dead-lettered.</param>
public sealed record MigrationVerification(long SourceCount, long TargetCount, long DeadLettered, long ExpectedTarget)
{
    public bool Matches => TargetCount == ExpectedTarget;

    public long Difference => TargetCount - ExpectedTarget;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"source has {SourceCount} rows, {DeadLettered} are dead-lettered, so the target should hold {ExpectedTarget} but holds {TargetCount} (difference {Difference})");
}

public sealed record MigrationProgress(string RunId, long Migrated, long Invalid, long Batches, string LastKey);

/// <param name="RowsRead">Rows read in this run (a resumed run does not re-read what an earlier one finished).</param>
public sealed record MigrationSummary(
    string RunId,
    string Name,
    long RowsRead,
    long Migrated,
    long Invalid,
    long Batches,
    string? ResumedFromKey,
    MigrationVerification Verification);

public sealed class MigrationHooks
{
    public Func<MigrationProgress, CancellationToken, Task>? OnProgress { get; init; }

    /// <summary>Called before every checkpoint write; throws when the run no longer owns its lease.</summary>
    public Func<CancellationToken, Task>? BeforeCheckpoint { get; init; }
}

/// <summary>
/// Copies a relational source into a Mongo collection in keyset batches. A batch is read, converted row by row, upserted
/// by <c>_id</c>, its invalid rows are dead-lettered, and only then is the key checkpointed, so a crash replays at most
/// one batch and replays are harmless. Invalid rows never stop the run. At the end the source count is compared with
/// the target count; a difference is reported, never hidden.
/// </summary>
public sealed class MigrationRunner(
    IRowSource source,
    IMigrationTarget target,
    ICheckpointStore checkpoints,
    IDeadLetterStore deadLetters,
    ILogger<MigrationRunner> logger)
{
    public async Task<MigrationSummary> RunAsync(
        MigrationDefinition definition,
        int batchSize,
        CancellationToken cancellationToken,
        MigrationHooks? hooks = null,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        runId ??= Guid.NewGuid().ToString("N");
        var key = definition.Target;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["Migration"] = definition.Name, ["MigrationRunId"] = runId });

        var checkpoint = await checkpoints.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var afterKey = checkpoint is null ? null : ToSourceKey(checkpoint.LastId, definition);
        var resumedFrom = afterKey is null ? null : Convert.ToString(afterKey, CultureInfo.InvariantCulture);
        logger.LogInformation("Migration {Name} starting {Mode}.", definition.Name, resumedFrom is null ? "from the beginning" : $"after key {resumedFrom}");

        long read = 0, migrated = 0, invalid = 0, batches = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await source.ReadBatchAsync(definition, afterKey, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Rows.Count == 0)
            {
                break;
            }

            var documents = new List<BsonDocument>(batch.Rows.Count);
            var migratedKeys = new List<string>(batch.Rows.Count);
            var rejected = new List<DeadLetterEntry>();
            foreach (var row in batch.Rows)
            {
                var mapped = RowMapper.Map(definition, row);
                if (mapped.Document is { } document)
                {
                    documents.Add(document);
                    migratedKeys.Add(mapped.KeyText);
                }
                else
                {
                    rejected.Add(new DeadLetterEntry(mapped.KeyText, "migration-row-invalid: " + string.Join("; ", mapped.Problems), mapped.PayloadHash));
                }
            }

            if (documents.Count > 0)
            {
                await target.UpsertAsync(definition.TargetCollection, documents, cancellationToken).ConfigureAwait(false);

                // A row that was invalid earlier and has since been fixed in the source is no longer outstanding.
                await deadLetters.RemoveAsync(key, migratedKeys, cancellationToken).ConfigureAwait(false);
            }

            // Dead letters first, checkpoint second: a crash in between replays the batch.
            await deadLetters.WriteAsync(key, rejected, runId, cancellationToken).ConfigureAwait(false);

            var lastKey = batch.Rows[^1].Key;
            await BeforeCheckpointAsync(hooks, cancellationToken).ConfigureAwait(false);
            await checkpoints.SaveAsync(key, ToCheckpointKey(lastKey), runId, cancellationToken).ConfigureAwait(false);

            read += batch.Rows.Count;
            migrated += documents.Count;
            invalid += rejected.Count;
            batches++;
            var lastKeyText = Convert.ToString(lastKey, CultureInfo.InvariantCulture) ?? string.Empty;
            if (hooks?.OnProgress is { } onProgress)
            {
                await onProgress(new MigrationProgress(runId, migrated, invalid, batches, lastKeyText), cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Batch {Batch} done: {Migrated} migrated, {Invalid} invalid, checkpoint at {Key}.", batches, documents.Count, rejected.Count, lastKeyText);

            if (!batch.HasMore)
            {
                break;
            }

            afterKey = lastKey;
        }

        await BeforeCheckpointAsync(hooks, cancellationToken).ConfigureAwait(false);
        await checkpoints.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

        var verification = await VerifyAsync(definition, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Migration {Name} read {Read} rows: {Migrated} migrated, {Invalid} invalid. Verification: {Verification}",
            definition.Name,
            read,
            migrated,
            invalid,
            verification.Matches ? "counts match" : verification.Describe());
        return new MigrationSummary(runId, definition.Name, read, migrated, invalid, batches, resumedFrom, verification);
    }

    private async Task<MigrationVerification> VerifyAsync(MigrationDefinition definition, CancellationToken cancellationToken)
    {
        var sourceCount = await source.CountAsync(definition, cancellationToken).ConfigureAwait(false);
        var targetCount = await target.CountAsync(definition.TargetCollection, cancellationToken).ConfigureAwait(false);
        var outstanding = await deadLetters.CountAsync(definition.Target, null, cancellationToken).ConfigureAwait(false);
        return new MigrationVerification(sourceCount, targetCount, outstanding, Math.Max(0, sourceCount - outstanding));
    }

    private static Task BeforeCheckpointAsync(MigrationHooks? hooks, CancellationToken cancellationToken) =>
        hooks?.BeforeCheckpoint is { } before ? before(cancellationToken) : Task.CompletedTask;

    private static BsonValue ToCheckpointKey(object key) => key switch
    {
        long number => new BsonInt64(number),
        string text => new BsonString(text),
        _ => throw new InvalidOperationException($"A source key of type {key.GetType().Name} cannot be checkpointed."),
    };

    private static object ToSourceKey(BsonValue stored, MigrationDefinition definition) => (definition.KeyType, stored.BsonType) switch
    {
        (KeyKind.Integer, BsonType.Int32 or BsonType.Int64) => stored.ToInt64(),
        (KeyKind.String, BsonType.String) => stored.AsString,
        _ => throw new InvalidOperationException(
            $"The checkpoint of migration '{definition.Name}' holds a {stored.BsonType} key but the definition's key is {definition.KeyType}; remove the checkpoint or fix the definition."),
    };
}
