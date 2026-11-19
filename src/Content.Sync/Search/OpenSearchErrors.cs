namespace Content.Sync.Search;

/// <summary>Translates OpenSearch client failures into the two exceptions the sync code understands.</summary>
internal static class OpenSearchErrors
{
    private static readonly int[] TransientStatuses = [429, 502, 503, 504];

    public static bool IsTransient(int status) => Array.IndexOf(TransientStatuses, status) >= 0;

    /// <summary>
    /// No status at all, or a transient one, means the cluster is unavailable (worth retrying); anything else is a
    /// refusal. A cancelled request is reported by the client as a failed response, so it is surfaced as the cancellation it is.
    /// </summary>
    public static Exception Translate(int? status, Exception? inner, string action, CancellationToken cancellationToken, string? serverReason = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detail = serverReason is null ? string.Empty : $": {serverReason}";
        if (status is null || IsTransient(status.Value))
        {
            return new SearchIndexUnavailableException($"OpenSearch was unavailable while {action} (status {status?.ToString() ?? "none"}){detail}", inner);
        }

        return new SearchIndexException($"OpenSearch refused the request while {action} (status {status}){detail}", inner);
    }
}
