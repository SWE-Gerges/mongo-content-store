using Content.Mongo.Coordination;
using Content.Sync.Configuration;
using MongoDB.Bson;

namespace Content.Sync.Indexing;

public enum RebuildStartOutcome
{
    Started,
    AlreadyRunning,
}

public sealed record RebuildStartResult(RebuildStartOutcome Outcome, string Collection, string? RunId);

public enum RollbackOutcome
{
    RolledBack,

    /// <summary>A sync or rebuild of the collection is active, so the alias must not move now.</summary>
    Busy,

    /// <summary>There is no safe version to go back to.</summary>
    Refused,
}

public sealed record RollbackAttempt(RollbackOutcome Outcome, RollbackResult? Result, string? Message);

/// <summary>Starts rebuilds, rolls back and reports on the versions behind an index alias.</summary>
public interface IIndexVersioning
{
    /// <summary>Starts a rebuild under the collection's run lease, or reports the run that holds it.</summary>
    Task<RebuildStartResult> TryStartRebuildAsync(string alias, SyncOptions options, CancellationToken cancellationToken);

    Task<IndexOverview> GetOverviewAsync(string alias, CancellationToken cancellationToken);

    Task<RollbackAttempt> RollbackAsync(string alias, int? version, CancellationToken cancellationToken);
}

internal sealed class IndexVersioningService(
    ISyncTargetCatalog catalog,
    RunCoordinator runs,
    ILeaseStore leases,
    IndexVersionManager manager) : IIndexVersioning
{
    public async Task<RebuildStartResult> TryStartRebuildAsync(string alias, SyncOptions options, CancellationToken cancellationToken)
    {
        var collection = Resolve(alias);
        var started = await runs.TryStartAsync(RunKinds.Rebuild, collection, new RebuildRequest(alias, options), cancellationToken).ConfigureAwait(false);
        return new RebuildStartResult(started.Started ? RebuildStartOutcome.Started : RebuildStartOutcome.AlreadyRunning, collection, started.RunId);
    }

    public Task<IndexOverview> GetOverviewAsync(string alias, CancellationToken cancellationToken)
    {
        Resolve(alias);
        return manager.GetOverviewAsync(alias, cancellationToken);
    }

    public async Task<RollbackAttempt> RollbackAsync(string alias, int? version, CancellationToken cancellationToken)
    {
        var collection = Resolve(alias);
        var lease = await runs.TryAcquireTargetAsync(collection, "rollback-" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return new RollbackAttempt(RollbackOutcome.Busy, null, $"A sync or rebuild of '{collection}' is active; the alias is not moved while it runs.");
        }

        try
        {
            var result = await manager.RollbackAsync(alias, version, cancellationToken).ConfigureAwait(false);
            return new RollbackAttempt(RollbackOutcome.RolledBack, result, null);
        }
        catch (RollbackRefusedException ex)
        {
            return new RollbackAttempt(RollbackOutcome.Refused, null, ex.Message);
        }
        finally
        {
            await leases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string Resolve(string alias) =>
        catalog.CollectionForAlias(alias) ?? throw new ArgumentException($"'{alias}' is not an index alias of this service.", nameof(alias));
}

/// <summary>What a rebuild run is started with.</summary>
public sealed record RebuildRequest(string Alias, SyncOptions Options);

/// <summary>Runs a rebuild of a collection's index. A build the verification refuses fails the run and keeps the new index for inspection.</summary>
internal sealed class IndexRebuildWorker(IndexVersionManager manager) : IRunWorker
{
    public string Kind => RunKinds.Rebuild;

    public async Task<BsonDocument?> ExecuteAsync(RunContext context, CancellationToken cancellationToken)
    {
        var request = (RebuildRequest)context.Parameters!;
        var result = await manager
            .RebuildAsync(
                request.Alias,
                context.Target,
                request.Options,
                new RebuildHooks
                {
                    OnProgress = (progress, token) => context.ReportAsync(
                        new Dictionary<string, long> { ["indexed"] = progress.Indexed, ["deadLettered"] = progress.DeadLettered, ["batches"] = progress.Batches },
                        progress.LastId,
                        token),
                    EnsureOwnership = context.EnsureLeaseAsync,
                },
                context.RunId,
                cancellationToken)
            .ConfigureAwait(false);

        var detail = new BsonDocument
        {
            { "alias", result.Alias },
            { "indexName", result.IndexName },
            { "version", result.Version },
            { "swapped", result.Swapped },
            { "previousIndex", result.PreviousIndex is null ? BsonNull.Value : result.PreviousIndex },
            { "resumed", result.Resumed },
            { "indexed", result.Sync.Indexed },
            { "deadLettered", result.Sync.DeadLettered },
            { "pruned", new BsonArray(result.Pruned) },
            { "verification", MongoIndexVersionStore.VerificationToDocument(result.Verification) },
        };

        if (!result.Swapped)
        {
            throw new RunFailedException($"Index '{result.IndexName}' failed verification and was not swapped in; it is kept for inspection. {result.Verification.Summary}", detail);
        }

        return detail;
    }
}
