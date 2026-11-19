using Content.Sync.Configuration;
using Content.Sync.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Content.Sync.Indexing;

/// <summary>Integration points of a rebuild.</summary>
public sealed class RebuildHooks
{
    /// <summary>Called after each settled batch of the build.</summary>
    public Func<SyncProgress, CancellationToken, Task>? OnProgress { get; init; }

    /// <summary>Called before every checkpoint write and right before the alias swap; throws when the run no longer owns its lease.</summary>
    public Func<CancellationToken, Task>? EnsureOwnership { get; init; }
}

/// <param name="Swapped">False when verification refused the new index and the alias stayed where it was.</param>
/// <param name="PreviousIndex">What the alias pointed at before a swap, if anything.</param>
/// <param name="Pruned">Indexes deleted after the swap.</param>
/// <param name="Resumed">The build continued an earlier, interrupted one instead of starting a new index.</param>
public sealed record RebuildResult(
    string Alias,
    string IndexName,
    int Version,
    bool Swapped,
    string? PreviousIndex,
    IndexVerification Verification,
    SyncRunSummary Sync,
    IReadOnlyList<string> Pruned,
    bool Resumed);

public sealed record RollbackResult(string Alias, string IndexName, int Version, string? PreviousIndex);

/// <summary>There is nothing safe to roll back to.</summary>
public sealed class RollbackRefusedException(string message) : Exception(message);

public sealed record IndexVersionInfo(
    string IndexName,
    int Version,
    IndexVersionState State,
    bool ExistsOnCluster,
    bool IsCurrent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SwappedInAt,
    DateTimeOffset? RetiredAt,
    IndexVerification? Verification);

/// <param name="Current">The physical index the alias points at, or null when the alias does not exist (or is still a plain index).</param>
/// <param name="LegacyIndex">The name is a concrete index from before versioning; the first successful rebuild replaces it atomically.</param>
public sealed record IndexOverview(
    string Alias,
    string? Current,
    bool LegacyIndex,
    IReadOnlyList<IndexVersionInfo> Versions,
    IndexVerification? LastVerification);

/// <summary>
/// Builds a collection into a new physical index <c>{alias}-v{N}</c>, verifies it, and only then points the alias at it
/// with a single alias request, so readers never see a missing alias. The previous versions that were live are kept for
/// rollback (the newest K, see <see cref="IndexVersioningOptions.RetainedVersions"/>); older ones and failed builds are
/// deleted after a successful swap. State about which index was live lives in the version store, the alias target on
/// the cluster is the authority and is reconciled into the store whenever it is looked at.
/// </summary>
public sealed class IndexVersionManager(
    ISearchIndexAdmin admin,
    SyncRunner runner,
    IIndexVersionStore versions,
    IndexVerifier verifier,
    IOptions<IndexVersioningOptions> options,
    TimeProvider clock,
    ILogger<IndexVersionManager> logger)
{
    public async Task<RebuildResult> RebuildAsync(
        string alias,
        string collection,
        SyncOptions syncOptions,
        RebuildHooks? hooks,
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        var aliasState = await admin.GetAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        var records = await ReconcileAsync(alias, aliasState, cancellationToken).ConfigureAwait(false);
        var (build, resumed) = await PickBuildAsync(alias, records, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Rebuild of {Alias} into {Index} {Mode}.", alias, build.IndexName, resumed ? "resuming an interrupted build" : "starting");
        var summary = await runner
            .RunAsync(
                collection,
                syncOptions,
                cancellationToken,
                new SyncRunHooks
                {
                    OnProgress = hooks?.OnProgress,
                    BeforeCheckpoint = hooks?.EnsureOwnership,
                    Target = new SyncRunTarget(build.IndexName, $"rebuild:{build.IndexName}", build.IndexName),
                },
                runId)
            .ConfigureAwait(false);

        var verification = await verifier.VerifyAsync(collection, build.IndexName, cancellationToken).ConfigureAwait(false);
        if (!verification.Passed)
        {
            await versions.SaveAsync(build with { State = IndexVersionState.Failed, Verification = verification }, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Index {Index} failed verification and was not swapped in: {Summary}", build.IndexName, verification.Summary);
            return new RebuildResult(alias, build.IndexName, build.Version, false, null, verification, summary, [], resumed);
        }

        if (hooks?.EnsureOwnership is { } ensure)
        {
            await ensure(cancellationToken).ConfigureAwait(false);
        }

        if (!await admin.IndexExistsAsync(build.IndexName, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Index '{build.IndexName}' disappeared before it could be swapped in.");
        }

        var current = await admin.GetAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        await admin.SwapAliasAsync(new AliasSwap(alias, build.IndexName, current.Targets, current.IsConcreteIndex), cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        foreach (var live in records.Where(r => r.State == IndexVersionState.Live && r.IndexName != build.IndexName))
        {
            await versions.SaveAsync(live with { State = IndexVersionState.Retired, RetiredAt = now }, cancellationToken).ConfigureAwait(false);
        }

        await versions
            .SaveAsync(build with { State = IndexVersionState.Live, SwappedInAt = now, RetiredAt = null, Verification = verification }, cancellationToken)
            .ConfigureAwait(false);

        var pruned = await PruneAsync(alias, build.IndexName, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Alias {Alias} now points at {Index}; pruned {Pruned}.", alias, build.IndexName, pruned.Count);
        return new RebuildResult(alias, build.IndexName, build.Version, true, current.IsConcreteIndex ? null : current.Targets.FirstOrDefault(), verification, summary, pruned, resumed);
    }

    /// <summary>Points the alias back at the most recently retired version that still exists (or the one asked for).</summary>
    /// <exception cref="RollbackRefusedException">No retained version is available.</exception>
    public async Task<RollbackResult> RollbackAsync(string alias, int? version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var aliasState = await admin.GetAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        var records = await ReconcileAsync(alias, aliasState, cancellationToken).ConfigureAwait(false);
        var candidates = records
            .Where(r => r.State == IndexVersionState.Retired)
            .OrderByDescending(r => r.RetiredAt ?? r.CreatedAt)
            .ThenByDescending(r => r.Version)
            .ToList();

        if (version is { } wanted)
        {
            candidates = candidates.Where(r => r.Version == wanted).ToList();
            if (candidates.Count == 0)
            {
                throw new RollbackRefusedException($"Version {wanted} of '{alias}' is not a retained version.");
            }
        }

        IndexVersionRecord? target = null;
        foreach (var candidate in candidates)
        {
            if (await admin.IndexExistsAsync(candidate.IndexName, cancellationToken).ConfigureAwait(false))
            {
                target = candidate;
                break;
            }

            logger.LogWarning("Retained index {Index} no longer exists on the cluster; forgetting it.", candidate.IndexName);
            await versions.DeleteAsync(candidate.IndexName, cancellationToken).ConfigureAwait(false);
        }

        if (target is null)
        {
            throw new RollbackRefusedException($"No retained version of '{alias}' is available to roll back to.");
        }

        var removed = aliasState.IsConcreteIndex ? [] : aliasState.Targets;
        await admin.SwapAliasAsync(new AliasSwap(alias, target.IndexName, removed, false), cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        foreach (var live in records.Where(r => r.State == IndexVersionState.Live))
        {
            await versions.SaveAsync(live with { State = IndexVersionState.Retired, RetiredAt = now }, cancellationToken).ConfigureAwait(false);
        }

        await versions.SaveAsync(target with { State = IndexVersionState.Live, SwappedInAt = now, RetiredAt = null }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Alias {Alias} rolled back to {Index}.", alias, target.IndexName);
        return new RollbackResult(alias, target.IndexName, target.Version, removed.FirstOrDefault());
    }

    public async Task<IndexOverview> GetOverviewAsync(string alias, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var aliasState = await admin.GetAliasAsync(alias, cancellationToken).ConfigureAwait(false);
        var records = await ReconcileAsync(alias, aliasState, cancellationToken).ConfigureAwait(false);
        var existing = (await admin.ListIndexesAsync(IndexVersionNaming.Prefix(alias), cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var current = aliasState.Exists && !aliasState.IsConcreteIndex ? aliasState.Targets.FirstOrDefault() : null;

        var infos = records
            .OrderByDescending(r => r.Version)
            .Select(r => new IndexVersionInfo(
                r.IndexName,
                r.Version,
                r.State,
                existing.Contains(r.IndexName),
                r.IndexName == current,
                r.CreatedAt,
                r.SwappedInAt,
                r.RetiredAt,
                r.Verification))
            .ToList();
        var last = records.Where(r => r.Verification is not null).OrderByDescending(r => r.Version).FirstOrDefault()?.Verification;
        return new IndexOverview(alias, current, aliasState.IsConcreteIndex, infos, last);
    }

    private async Task<(IndexVersionRecord Build, bool Resumed)> PickBuildAsync(string alias, IReadOnlyList<IndexVersionRecord> records, CancellationToken cancellationToken)
    {
        var live = records.FirstOrDefault(r => r.State == IndexVersionState.Live);
        var unfinished = records
            .Where(r => r.State == IndexVersionState.Building && (live is null || r.Version > live.Version))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
        if (unfinished is not null)
        {
            return (unfinished, true);
        }

        var onCluster = await admin.ListIndexesAsync(IndexVersionNaming.Prefix(alias), cancellationToken).ConfigureAwait(false);
        var highest = records.Select(r => r.Version)
            .Concat(onCluster.Select(name => IndexVersionNaming.TryParse(alias, name)).OfType<int>())
            .DefaultIfEmpty(0)
            .Max();
        var next = highest + 1;
        var build = new IndexVersionRecord(IndexVersionNaming.For(alias, next), alias, next, IndexVersionState.Building, clock.GetUtcNow(), null, null, null);
        await versions.SaveAsync(build, cancellationToken).ConfigureAwait(false);
        return (build, false);
    }

    /// <summary>Makes the stored states agree with what the alias actually points at.</summary>
    private async Task<IReadOnlyList<IndexVersionRecord>> ReconcileAsync(string alias, AliasState aliasState, CancellationToken cancellationToken)
    {
        var stored = await versions.ListAsync(alias, cancellationToken).ConfigureAwait(false);
        var targets = aliasState.Exists && !aliasState.IsConcreteIndex ? aliasState.Targets.ToHashSet(StringComparer.Ordinal) : [];
        var now = clock.GetUtcNow();
        var result = new List<IndexVersionRecord>(stored.Count);

        foreach (var record in stored)
        {
            var corrected = record;
            if (record.State == IndexVersionState.Live && !targets.Contains(record.IndexName))
            {
                corrected = record with { State = IndexVersionState.Retired, RetiredAt = record.RetiredAt ?? now };
            }
            else if (record.State != IndexVersionState.Live && targets.Contains(record.IndexName))
            {
                corrected = record with { State = IndexVersionState.Live, SwappedInAt = record.SwappedInAt ?? now, RetiredAt = null };
            }

            if (corrected != record)
            {
                await versions.SaveAsync(corrected, cancellationToken).ConfigureAwait(false);
            }

            result.Add(corrected);
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> PruneAsync(string alias, string liveIndex, CancellationToken cancellationToken)
    {
        var records = await versions.ListAsync(alias, cancellationToken).ConfigureAwait(false);
        var retired = records
            .Where(r => r.State == IndexVersionState.Retired)
            .OrderByDescending(r => r.RetiredAt ?? r.CreatedAt)
            .ThenByDescending(r => r.Version)
            .ToList();

        var doomed = retired.Skip(options.Value.RetainedVersions)
            .Concat(records.Where(r => (r.State is IndexVersionState.Failed or IndexVersionState.Building) && r.IndexName != liveIndex))
            .ToList();

        var deleted = new List<string>();
        foreach (var record in doomed)
        {
            await admin.DeleteIndexAsync(record.IndexName, cancellationToken).ConfigureAwait(false);
            await versions.DeleteAsync(record.IndexName, cancellationToken).ConfigureAwait(false);
            deleted.Add(record.IndexName);
        }

        return deleted;
    }
}
