using Content.Mongo.Coordination;
using Content.Sync.Checkpoints;
using Content.Sync.Configuration;
using Content.Sync.DeadLetters;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Content.Sync.State;

public enum SyncState
{
    Idle,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>The instance running it went away before it finished; start it again to resume from the checkpoint.</summary>
    Interrupted,
}

/// <summary>Point-in-time view of a collection's latest sync or index rebuild, readable from any instance.</summary>
/// <param name="Kind"><c>sync</c> or <c>rebuild</c>; null when the collection never ran.</param>
/// <param name="LastId">The last id checkpointed by the latest run, else the persisted checkpoint, else null.</param>
/// <param name="DeadLettersTotal">All dead letters stored for the collection.</param>
public sealed record SyncStatus(
    string Collection,
    string? Kind,
    SyncState State,
    string? RunId,
    string? LastId,
    long Indexed,
    long DeadLettered,
    long DeadLettersTotal,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? LastError);

public enum SyncStartOutcome
{
    Started,
    AlreadyRunning,
}

public sealed record SyncStartResult(SyncStartOutcome Outcome, string? RunId);

/// <summary>Starts, cancels and reports on runs of a collection. At most one run per collection is active across all instances.</summary>
public interface ISyncCoordinator
{
    /// <summary>The configured defaults with the per-run overrides applied (validated by the caller).</summary>
    SyncOptions CreateRunOptions(int? batchSize, TimeSpan? interBatchDelay);

    Task<SyncStartResult> TryStartAsync(string collection, SyncOptions options, CancellationToken cancellationToken);

    /// <returns>True when an active run was asked to stop.</returns>
    Task<bool> CancelAsync(string collection, CancellationToken cancellationToken);

    Task<SyncStatus> GetStatusAsync(string collection, CancellationToken cancellationToken);
}

internal sealed class SyncCoordinator(
    RunCoordinator runs,
    ICheckpointStore checkpoints,
    IDeadLetterStore deadLetters,
    IOptions<SyncOptions> defaults) : ISyncCoordinator
{
    public SyncOptions CreateRunOptions(int? batchSize, TimeSpan? interBatchDelay)
    {
        var options = defaults.Value.Clone();
        options.BatchSize = batchSize ?? options.BatchSize;
        options.InterBatchDelay = interBatchDelay ?? options.InterBatchDelay;
        return options;
    }

    public async Task<SyncStartResult> TryStartAsync(string collection, SyncOptions options, CancellationToken cancellationToken)
    {
        var started = await runs.TryStartAsync(RunKinds.Sync, collection, options, cancellationToken).ConfigureAwait(false);
        return new SyncStartResult(started.Started ? SyncStartOutcome.Started : SyncStartOutcome.AlreadyRunning, started.RunId);
    }

    public Task<bool> CancelAsync(string collection, CancellationToken cancellationToken) =>
        runs.CancelAsync(collection, cancellationToken);

    public async Task<SyncStatus> GetStatusAsync(string collection, CancellationToken cancellationToken)
    {
        var status = await runs.GetStatusAsync(collection, cancellationToken).ConfigureAwait(false);
        var checkpoint = await checkpoints.GetAsync(collection, cancellationToken).ConfigureAwait(false);
        var persistedLastId = checkpoint is null ? null : Search.HotelSearchDocumentMapper.RenderId(checkpoint.LastId);
        var total = await deadLetters.CountAsync(collection, null, cancellationToken).ConfigureAwait(false);

        if (status is null)
        {
            return new SyncStatus(collection, null, SyncState.Idle, null, persistedLastId, 0, 0, total, null, null, null);
        }

        var record = status.Record;
        return new SyncStatus(
            collection,
            record.Kind,
            Enum.Parse<SyncState>(status.EffectiveState.ToString()),
            record.Id,
            record.LastId ?? persistedLastId,
            record.Counters.GetValueOrDefault("indexed"),
            record.Counters.GetValueOrDefault("deadLettered"),
            total,
            record.StartedAt,
            record.FinishedAt,
            record.LastError);
    }
}

/// <summary>Runs a plain sync of a collection into its search index.</summary>
internal sealed class SyncRunWorker(SyncRunner runner) : IRunWorker
{
    public string Kind => RunKinds.Sync;

    public async Task<BsonDocument?> ExecuteAsync(RunContext context, CancellationToken cancellationToken)
    {
        var options = (SyncOptions)context.Parameters!;
        var summary = await runner
            .RunAsync(
                context.Target,
                options,
                cancellationToken,
                new SyncRunHooks
                {
                    OnProgress = (progress, token) => context.ReportAsync(
                        new Dictionary<string, long> { ["indexed"] = progress.Indexed, ["deadLettered"] = progress.DeadLettered, ["batches"] = progress.Batches },
                        progress.LastId,
                        token),
                    BeforeCheckpoint = context.EnsureLeaseAsync,
                },
                context.RunId)
            .ConfigureAwait(false);

        return new BsonDocument
        {
            { "indexed", summary.Indexed },
            { "deadLettered", summary.DeadLettered },
            { "batches", summary.Batches },
            { "resumedFromId", summary.ResumedFromId is null ? BsonNull.Value : summary.ResumedFromId },
        };
    }
}
