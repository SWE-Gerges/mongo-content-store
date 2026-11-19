namespace Content.Sync.DeadLetters;

/// <summary>A document the run could not index and is parking for inspection.</summary>
/// <param name="DocId">The source <c>_id</c> rendered as text.</param>
/// <param name="Reason">Why it could not be indexed.</param>
/// <param name="PayloadHash">SHA-256 (hex) of the source document, to recognise a changed payload on replay.</param>
public sealed record DeadLetterEntry(string DocId, string Reason, string PayloadHash);

/// <summary>A stored dead letter as <c>_sync_deadletter</c> holds it.</summary>
public sealed record DeadLetterRecord(
    string Collection,
    string DocId,
    string Reason,
    DateTimeOffset FirstSeenAt,
    int Attempts,
    string PayloadHash);

/// <summary>One page of dead letters, newest first. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record DeadLetterPage(IReadOnlyList<DeadLetterRecord> Items, string? NextCursor);

public interface IDeadLetterStore
{
    /// <summary>
    /// Durably records the entries (upsert per collection and document: a repeat bumps <c>Attempts</c> and keeps
    /// <c>FirstSeenAt</c>). The returned task completes only once they are stored.
    /// </summary>
    /// <param name="scope">Labels the run that parked them (the target index of a build); the latest writer wins.</param>
    Task WriteAsync(string collection, IReadOnlyCollection<DeadLetterEntry> entries, string? scope, CancellationToken cancellationToken);

    /// <summary>Lists newest first with keyset paging; pass the previous page's <c>NextCursor</c> to continue.</summary>
    /// <exception cref="Content.Store.InvalidCursorException">The cursor is not one this store issued.</exception>
    Task<DeadLetterPage> ListAsync(string? collection, string? cursor, int pageSize, CancellationToken cancellationToken);

    /// <param name="scope">When given, counts only the dead letters last written under that scope.</param>
    Task<long> CountAsync(string collection, string? scope, CancellationToken cancellationToken);

    /// <summary>Forgets dead letters whose documents have since been handled, so counts reflect what is still outstanding.</summary>
    Task RemoveAsync(string collection, IReadOnlyCollection<string> docIds, CancellationToken cancellationToken);
}
