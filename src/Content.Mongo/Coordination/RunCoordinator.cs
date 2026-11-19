using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Content.Mongo.Coordination;

/// <summary>A worker finished its work but the outcome is a failure it wants recorded with details (for example a rejected verification).</summary>
public sealed class RunFailedException(string message, BsonDocument? detail = null) : Exception(message)
{
    public BsonDocument? Detail { get; } = detail;
}

/// <summary>The lease was taken over by someone else; this owner must stop writing.</summary>
public sealed class LeaseLostException(string key)
    : Exception($"The lease on '{key}' is no longer held by this run.");

/// <summary>
/// Executes one kind of run (sync, index rebuild, migration). Workers are registered in dependency injection and
/// selected by <see cref="Kind"/>, so the coordination code never depends on what the runs do.
/// </summary>
public interface IRunWorker
{
    string Kind { get; }

    /// <returns>Kind-specific result stored on the run record, or null.</returns>
    Task<BsonDocument?> ExecuteAsync(RunContext context, CancellationToken cancellationToken);
}

/// <summary>What a worker sees of the run it is executing.</summary>
public sealed class RunContext
{
    private readonly Func<IReadOnlyDictionary<string, long>, string?, CancellationToken, Task> _report;
    private readonly Func<CancellationToken, Task> _ensureLease;

    internal RunContext(
        string runId,
        string kind,
        string target,
        object? parameters,
        Func<IReadOnlyDictionary<string, long>, string?, CancellationToken, Task> report,
        Func<CancellationToken, Task> ensureLease)
    {
        RunId = runId;
        Kind = kind;
        Target = target;
        Parameters = parameters;
        _report = report;
        _ensureLease = ensureLease;
    }

    public string RunId { get; }

    public string Kind { get; }

    public string Target { get; }

    /// <summary>The in-memory arguments the run was started with.</summary>
    public object? Parameters { get; }

    /// <summary>Persists progress counters and the last checkpointed key where any instance can read them.</summary>
    public Task ReportAsync(IReadOnlyDictionary<string, long> counters, string? lastId, CancellationToken cancellationToken) =>
        _report(counters, lastId, cancellationToken);

    /// <summary>
    /// Renews the lease and throws <see cref="LeaseLostException"/> if it is gone. Call it immediately before a
    /// write that must not come from a deposed owner (a checkpoint).
    /// </summary>
    public Task EnsureLeaseAsync(CancellationToken cancellationToken) => _ensureLease(cancellationToken);
}

public sealed record RunStartResult(bool Started, string? RunId);

/// <summary>A run record together with the state it effectively has now (an orphaned active run reads as interrupted).</summary>
public sealed record RunStatus(RunRecord Record, RunState EffectiveState);

internal sealed record RunJob(string RunId, string Kind, string Target, object? Parameters, Lease Lease);

/// <summary>
/// Starts, cancels and reports on runs across instances. Starting takes the target's lease with one atomic update,
/// so exactly one instance can run a target at a time; the run itself executes on the instance that took the lease
/// (see <see cref="RunHost"/>). State lives in MongoDB, so any instance can report on or cancel any run.
/// </summary>
public sealed class RunCoordinator(
    ILeaseStore leases,
    IRunStore runs,
    IOptions<CoordinationOptions> options,
    TimeProvider clock,
    InstanceIdentity identity)
{
    private readonly Channel<RunJob> _queue = Channel.CreateUnbounded<RunJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _local = new(StringComparer.Ordinal);

    internal ChannelReader<RunJob> Jobs => _queue.Reader;

    internal ILeaseStore Leases => leases;

    internal IRunStore Runs => runs;

    internal CoordinationOptions Options => options.Value;

    internal TimeProvider Clock => clock;

    public async Task<RunStartResult> TryStartAsync(string kind, string target, object? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var runId = Guid.NewGuid().ToString("N");
        var lease = await TryAcquireTargetAsync(target, runId, cancellationToken).ConfigureAwait(false);

        if (lease is null)
        {
            var holder = await leases.GetAsync(target, cancellationToken).ConfigureAwait(false);
            return new RunStartResult(false, holder?.RunId);
        }

        try
        {
            // Holding the lease means any run still marked active belongs to an owner that is gone.
            await runs.MarkInterruptedAsync(target, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await runs.CreateAsync(
                    new RunRecord(
                        runId,
                        kind,
                        target,
                        RunState.Queued,
                        identity.Id,
                        clock.GetUtcNow(),
                        null,
                        null,
                        new Dictionary<string, long>(),
                        null,
                        null,
                        false,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await leases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _queue.Writer.TryWrite(new RunJob(runId, kind, target, parameters, lease));
        return new RunStartResult(true, runId);
    }

    /// <summary>
    /// Takes the target's lease for an action that is not a run (a rollback), applying the same rule as starting a run:
    /// the lease of a run that has already ended does not block it.
    /// </summary>
    /// <returns>The lease, or null when a live holder has the target.</returns>
    public async Task<Lease?> TryAcquireTargetAsync(string target, string holderId, CancellationToken cancellationToken)
    {
        var lease = await leases.TryAcquireAsync(target, identity.Id, holderId, options.Value.LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (lease is null && await ReleaseFinishedHolderAsync(target, cancellationToken).ConfigureAwait(false))
        {
            lease = await leases.TryAcquireAsync(target, identity.Id, holderId, options.Value.LeaseDuration, cancellationToken).ConfigureAwait(false);
        }

        return lease;
    }

    /// <summary>Asks the active run of the target to stop, wherever it executes.</summary>
    /// <returns>True when there was an active run.</returns>
    public async Task<bool> CancelAsync(string target, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(target, cancellationToken).ConfigureAwait(false);
        return status is not null && await CancelRunAsync(status.Record.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks one run to stop, wherever it executes.</summary>
    /// <returns>True when the run exists and was still active.</returns>
    public async Task<bool> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        var status = await GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (status is not { EffectiveState: RunState.Queued or RunState.Running })
        {
            return false;
        }

        if (!await runs.RequestCancelAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (_local.TryGetValue(runId, out var local))
        {
            await local.CancelAsync().ConfigureAwait(false);
        }

        return true;
    }

    public async Task<RunStatus?> GetStatusAsync(string target, CancellationToken cancellationToken)
    {
        var latest = await runs.GetLatestAsync(target, cancellationToken).ConfigureAwait(false);
        return latest is null ? null : await ToStatusAsync(latest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunStatus?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        var record = await runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : await ToStatusAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A run records that it has ended before it releases its lease, so for a moment a finished run still holds the target.
    /// That lease is released on its behalf, which lets a caller who saw the run finish start the next one at once.
    /// </summary>
    /// <returns>True when the target may be free now; false when a run that is still active (or a holder with no run record, such as a rollback) holds it.</returns>
    private async Task<bool> ReleaseFinishedHolderAsync(string target, CancellationToken cancellationToken)
    {
        var holder = await leases.GetAsync(target, cancellationToken).ConfigureAwait(false);
        if (holder is null)
        {
            return true;
        }

        var run = await runs.GetAsync(holder.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.IsActive)
        {
            return false;
        }

        await leases.ReleaseAsync(new Lease(target, holder.OwnerId, holder.RunId), cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal void RegisterLocal(string runId, CancellationTokenSource source) => _local[runId] = source;

    internal void UnregisterLocal(string runId) => _local.TryRemove(runId, out _);

    private async Task<RunStatus> ToStatusAsync(RunRecord record, CancellationToken cancellationToken)
    {
        if (!record.IsActive)
        {
            return new RunStatus(record, record.State);
        }

        var holder = await leases.GetAsync(record.Target, cancellationToken).ConfigureAwait(false);
        var alive = holder is not null && holder.RunId == record.Id && holder.ExpiresAt > clock.GetUtcNow();
        return new RunStatus(record, alive ? record.State : RunState.Interrupted);
    }
}
