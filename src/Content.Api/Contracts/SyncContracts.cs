using Content.Sync.DeadLetters;
using Content.Sync.State;

namespace Content.Api.Contracts;

/// <summary>Optional per-run overrides of the configured sync defaults.</summary>
public sealed record StartSyncRequest(int? BatchSize, int? InterBatchDelayMs);

public sealed record SyncStatusDto(
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
    string? LastError)
{
    public static SyncStatusDto From(SyncStatus status) => new(
        status.Collection,
        status.Kind,
        status.State,
        status.RunId,
        status.LastId,
        status.Indexed,
        status.DeadLettered,
        status.DeadLettersTotal,
        status.StartedAt,
        status.FinishedAt,
        status.LastError);
}

public sealed record DeadLetterDto(string Collection, string DocId, string Reason, DateTimeOffset FirstSeenAt, int Attempts, string PayloadHash)
{
    public static DeadLetterDto From(DeadLetterRecord record) =>
        new(record.Collection, record.DocId, record.Reason, record.FirstSeenAt, record.Attempts, record.PayloadHash);
}

public sealed record DeadLetterPageDto(IReadOnlyList<DeadLetterDto> Items, string? NextCursor);
