using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Content.Store.Configuration;

/// <summary>
/// Content store settings. Suppliers are plain configured keys: adding one adds a collection
/// (<c>hotel_{key}</c>), its indexes and a sync target without a code change.
/// </summary>
public sealed class ContentStoreOptions
{
    public const string SectionName = "ContentStore";

    public List<string> Suppliers { get; set; } = [];

    public int DefaultPageSize { get; set; } = 100;

    public int MaxPageSize { get; set; } = 1000;

    public int MaxIdsPerRequest { get; set; } = 500;

    public int MaxSearchResults { get; set; } = 100;

    /// <summary>Server-side time limit applied to name searches, which can scan.</summary>
    public TimeSpan SearchMaxTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Server-side time limit applied to lookups by id or code, so a slow fallback read cannot hold a request open.</summary>
    public TimeSpan ReadMaxTime { get; set; } = TimeSpan.FromSeconds(5);
}

internal sealed class ContentStoreOptionsValidator : IValidateOptions<ContentStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, ContentStoreOptions options)
    {
        var failures = new List<string>();

        if (options.Suppliers.Count == 0)
        {
            failures.Add("ContentStore:Suppliers must list at least one supplier key.");
        }

        foreach (var key in options.Suppliers.Where(key => !SupplierKey.IsValid(key)))
        {
            failures.Add($"ContentStore:Suppliers contains an invalid supplier key '{key}' (use lowercase letters, digits and single hyphens).");
        }

        if (options.Suppliers.Count != options.Suppliers.Distinct(StringComparer.Ordinal).Count())
        {
            failures.Add("ContentStore:Suppliers must not contain duplicates.");
        }

        if (options.DefaultPageSize < 1 || options.DefaultPageSize > options.MaxPageSize)
        {
            failures.Add("ContentStore:DefaultPageSize must be between 1 and MaxPageSize.");
        }

        if (options.MaxPageSize is < 1 or > 10_000)
        {
            failures.Add("ContentStore:MaxPageSize must be between 1 and 10000.");
        }

        if (options.MaxIdsPerRequest is < 1 or > 10_000)
        {
            failures.Add("ContentStore:MaxIdsPerRequest must be between 1 and 10000.");
        }

        if (options.MaxSearchResults is < 1 or > 1000)
        {
            failures.Add("ContentStore:MaxSearchResults must be between 1 and 1000.");
        }

        if (options.ReadMaxTime <= TimeSpan.Zero)
        {
            failures.Add("ContentStore:ReadMaxTime must be positive.");
        }

        if (options.SearchMaxTime <= TimeSpan.Zero)
        {
            failures.Add("ContentStore:SearchMaxTime must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Rules for supplier keys, which end up inside collection and index names.</summary>
public static partial class SupplierKey
{
    public const int MaxLength = 40;

    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && Pattern().IsMatch(key);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
