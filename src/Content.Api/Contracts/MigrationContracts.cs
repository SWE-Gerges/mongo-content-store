using Content.Migration;
using Content.Mongo.Coordination;

namespace Content.Api.Contracts;

/// <summary>Starts a configured migration, by name. The batch size overrides the configured one.</summary>
public sealed record StartMigrationRequest(string? Migration, int? BatchSize);

public sealed record MigrationVerificationDto(long SourceCount, long TargetCount, long DeadLettered, long ExpectedTarget, bool Matches, long Difference)
{
    public static MigrationVerificationDto? From(MigrationVerification? verification) => verification is null
        ? null
        : new MigrationVerificationDto(
            verification.SourceCount,
            verification.TargetCount,
            verification.DeadLettered,
            verification.ExpectedTarget,
            verification.Matches,
            verification.Difference);
}

/// <param name="LastKey">The last source key whose batch was checkpointed; where a resume would continue.</param>
/// <param name="Verification">Present once the run has compared the source count with the target count.</param>
public sealed record MigrationRunDto(
    string RunId,
    string Migration,
    RunState State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long Migrated,
    long Invalid,
    long Batches,
    string? LastKey,
    string? LastError,
    string? ResumedFromKey,
    MigrationVerificationDto? Verification)
{
    public static MigrationRunDto From(MigrationRunStatus status) => new(
        status.RunId,
        status.Migration,
        status.State,
        status.RequestedAt,
        status.StartedAt,
        status.FinishedAt,
        status.Migrated,
        status.Invalid,
        status.Batches,
        status.LastKey,
        status.LastError,
        status.ResumedFromKey,
        MigrationVerificationDto.From(status.Verification));
}
