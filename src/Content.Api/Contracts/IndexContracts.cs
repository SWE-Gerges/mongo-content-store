using Content.Sync.Indexing;
using Content.Sync.State;

namespace Content.Api.Contracts;

public sealed record RebuildAcceptedDto(string Alias, string Collection, string RunId);

public sealed record VerificationCheckDto(string Name, bool Passed, string Detail);

public sealed record IndexVerificationDto(
    bool Passed,
    DateTimeOffset VerifiedAt,
    long SourceCount,
    long IndexCount,
    long DeadLetters,
    IReadOnlyList<VerificationCheckDto> Checks)
{
    public static IndexVerificationDto? From(IndexVerification? verification) => verification is null
        ? null
        : new IndexVerificationDto(
            verification.Passed,
            verification.VerifiedAt,
            verification.SourceCount,
            verification.IndexCount,
            verification.DeadLetters,
            verification.Checks.Select(c => new VerificationCheckDto(c.Name, c.Passed, c.Detail)).ToList());
}

public sealed record IndexVersionDto(
    string IndexName,
    int Version,
    IndexVersionState State,
    bool ExistsOnCluster,
    bool IsCurrent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SwappedInAt,
    DateTimeOffset? RetiredAt,
    IndexVerificationDto? Verification);

/// <summary>The latest sync or rebuild run of the index collection, so a caller can follow a rebuild it started.</summary>
public sealed record IndexRunDto(
    string? Kind,
    SyncState State,
    string? RunId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? LastError,
    long Indexed,
    long DeadLettered);

/// <param name="Current">The physical index the alias points at; null when the alias does not exist or is still a plain index.</param>
/// <param name="Retained">Previous versions that were live once and still exist, newest first: the rollback targets.</param>
public sealed record IndexOverviewDto(
    string Alias,
    string Collection,
    string? Current,
    bool LegacyIndex,
    IReadOnlyList<string> Retained,
    IReadOnlyList<IndexVersionDto> Versions,
    IndexVerificationDto? LastVerification,
    IndexRunDto? LatestRun)
{
    public static IndexOverviewDto From(IndexOverview overview, string collection, SyncStatus run) => new(
        overview.Alias,
        collection,
        overview.Current,
        overview.LegacyIndex,
        overview.Versions.Where(v => v.State == IndexVersionState.Retired && v.ExistsOnCluster).Select(v => v.IndexName).ToList(),
        overview.Versions
            .Select(v => new IndexVersionDto(v.IndexName, v.Version, v.State, v.ExistsOnCluster, v.IsCurrent, v.CreatedAt, v.SwappedInAt, v.RetiredAt, IndexVerificationDto.From(v.Verification)))
            .ToList(),
        IndexVerificationDto.From(overview.LastVerification),
        run.Kind is null
            ? null
            : new IndexRunDto(run.Kind, run.State, run.RunId, run.StartedAt, run.FinishedAt, run.LastError, run.Indexed, run.DeadLettered));
}

/// <summary>Optionally names the retained version to go back to; by default the most recently retired one.</summary>
public sealed record RollbackRequest(int? Version);

public sealed record RollbackResultDto(string Alias, string Current, int Version, string? PreviousIndex);
