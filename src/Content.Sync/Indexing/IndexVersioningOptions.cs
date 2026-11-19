using Microsoft.Extensions.Options;

namespace Content.Sync.Indexing;

/// <summary>How many old versions to keep and how strictly a new one is judged before it replaces the live one.</summary>
public sealed class IndexVersioningOptions
{
    public const string SectionName = "IndexVersioning";

    /// <summary>Previous versions kept for rollback after a swap (the live one is not counted); older ones are deleted.</summary>
    public int RetainedVersions { get; set; } = 2;

    /// <summary>Allowed difference between the index count and the expected count, as a fraction of the source count.</summary>
    public double CountTolerance { get; set; } = 0.001;

    /// <summary>Most documents a build may have dead-lettered and still be accepted.</summary>
    public long MaxDeadLetters { get; set; } = 100;

    /// <summary>Source documents compared field by field with their copies in the new index. Zero skips the spot check.</summary>
    public int SampleSize { get; set; } = 25;
}

internal sealed class IndexVersioningOptionsValidator : IValidateOptions<IndexVersioningOptions>
{
    public ValidateOptionsResult Validate(string? name, IndexVersioningOptions options)
    {
        var failures = new List<string>();

        if (options.RetainedVersions is < 1 or > 20)
        {
            failures.Add("IndexVersioning:RetainedVersions must be between 1 and 20 (at least one version must remain to roll back to).");
        }

        if (double.IsNaN(options.CountTolerance) || options.CountTolerance is < 0 or >= 1)
        {
            failures.Add("IndexVersioning:CountTolerance must be at least 0 and below 1.");
        }

        if (options.MaxDeadLetters < 0)
        {
            failures.Add("IndexVersioning:MaxDeadLetters must not be negative.");
        }

        if (options.SampleSize is < 0 or > 1000)
        {
            failures.Add("IndexVersioning:SampleSize must be between 0 and 1000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
