using Microsoft.Extensions.Options;

namespace Content.Sync.Configuration;

/// <summary>Tuning for one sync run. The configured values are the defaults; a run may override a few.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Documents read from Mongo (one keyset page) between checkpoints.</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>Optional pause between batches to leave headroom for live traffic.</summary>
    public TimeSpan InterBatchDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Documents per bulk request.</summary>
    public int BulkChunkSize { get; set; } = 500;

    /// <summary>Bulk requests in flight at once for one batch.</summary>
    public int BulkMaxParallelism { get; set; } = 2;

    /// <summary>Total attempts for one Mongo page read before the run aborts.</summary>
    public int MaxReadAttempts { get; set; } = 5;

    public TimeSpan ReadBackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for a single read backoff delay.</summary>
    public TimeSpan ReadBackoffCap { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Total attempts for one bulk request when the search cluster is unavailable.</summary>
    public int MaxBulkAttempts { get; set; } = 4;

    public TimeSpan BulkBackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan BulkBackoffCap { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause before the single retry of items the cluster rejected as retryable (429/503).</summary>
    public TimeSpan ItemRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public SyncOptions Clone() => (SyncOptions)MemberwiseClone();
}

internal sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions options)
    {
        var failures = new List<string>();

        if (options.BatchSize is < 1 or > 100_000)
        {
            failures.Add("Sync:BatchSize must be between 1 and 100000.");
        }

        if (options.BulkChunkSize is < 1 or > 10_000)
        {
            failures.Add("Sync:BulkChunkSize must be between 1 and 10000.");
        }

        if (options.BulkMaxParallelism is < 1 or > 16)
        {
            failures.Add("Sync:BulkMaxParallelism must be between 1 and 16.");
        }

        if (options.MaxReadAttempts is < 1 or > 20)
        {
            failures.Add("Sync:MaxReadAttempts must be between 1 and 20.");
        }

        if (options.MaxBulkAttempts is < 1 or > 20)
        {
            failures.Add("Sync:MaxBulkAttempts must be between 1 and 20.");
        }

        foreach (var (label, value) in new[]
                 {
                     ("InterBatchDelay", options.InterBatchDelay),
                     ("ReadBackoffBase", options.ReadBackoffBase),
                     ("ReadBackoffCap", options.ReadBackoffCap),
                     ("BulkBackoffBase", options.BulkBackoffBase),
                     ("BulkBackoffCap", options.BulkBackoffCap),
                     ("ItemRetryDelay", options.ItemRetryDelay),
                 })
        {
            if (value < TimeSpan.Zero)
            {
                failures.Add($"Sync:{label} must not be negative.");
            }
        }

        if (options.ReadBackoffCap > TimeSpan.FromSeconds(60))
        {
            failures.Add("Sync:ReadBackoffCap must not exceed 60 seconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
