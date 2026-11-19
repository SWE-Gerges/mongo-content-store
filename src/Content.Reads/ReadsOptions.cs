using Content.Store;
using Content.Store.Configuration;
using Content.Sync.Search;
using Microsoft.Extensions.Options;

namespace Content.Reads;

/// <summary>Where one supplier's content is read from: its Mongo collection and the search index alias that mirrors it.</summary>
public sealed class ReadRoute
{
    public string Supplier { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    public string IndexAlias { get; set; } = string.Empty;
}

/// <summary>
/// Read path settings. <see cref="Routes"/> is the route table: every configured supplier needs an explicit entry,
/// because a supplier without one must be an error at startup, never a silent fall back to some default.
/// </summary>
public sealed class ReadsOptions
{
    public const string SectionName = "Reads";

    public List<ReadRoute> Routes { get; set; } = [];

    /// <summary>How long the search index may take to answer before the read is served from MongoDB instead.</summary>
    public TimeSpan IndexTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>The rules a route table must satisfy; pure, so every failure can be tested without a host.</summary>
public static class ReadRouteRules
{
    /// <returns>Every problem found, empty when the table is sound.</returns>
    public static IReadOnlyList<string> Check(
        IReadOnlyCollection<string> suppliers,
        IReadOnlyCollection<ReadRoute> routes,
        Func<string, string> collectionFor,
        Func<string, string> aliasFor)
    {
        var problems = new List<string>();
        var configured = suppliers.ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in routes.GroupBy(r => r.Supplier, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"Reads:Routes lists supplier '{duplicate.Key}' {duplicate.Count()} times.");
        }

        foreach (var duplicate in routes.GroupBy(r => r.Collection, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"Reads:Routes sends {duplicate.Count()} suppliers to collection '{duplicate.Key}'.");
        }

        foreach (var duplicate in routes.GroupBy(r => r.IndexAlias, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"Reads:Routes sends {duplicate.Count()} suppliers to index alias '{duplicate.Key}'.");
        }

        foreach (var supplier in configured.Where(s => routes.All(r => r.Supplier != s)).Order(StringComparer.Ordinal))
        {
            problems.Add($"Supplier '{supplier}' is configured but has no route in Reads:Routes.");
        }

        foreach (var route in routes)
        {
            if (!configured.Contains(route.Supplier))
            {
                problems.Add($"Reads:Routes has a route for '{route.Supplier}', which is not a configured supplier (a dangling route).");
                continue;
            }

            var expectedCollection = collectionFor(route.Supplier);
            if (!string.Equals(route.Collection, expectedCollection, StringComparison.Ordinal))
            {
                problems.Add($"Route for '{route.Supplier}' names collection '{route.Collection}', but the content store reads '{expectedCollection}'.");
            }

            var expectedAlias = aliasFor(expectedCollection);
            if (!string.Equals(route.IndexAlias, expectedAlias, StringComparison.Ordinal))
            {
                problems.Add($"Route for '{route.Supplier}' names index alias '{route.IndexAlias}', but sync serves that collection as '{expectedAlias}'.");
            }
        }

        return problems;
    }
}

internal sealed class ReadsOptionsValidator(
    IOptions<ContentStoreOptions> contentStore,
    ICollectionNaming naming,
    SearchIndexNaming indexNaming) : IValidateOptions<ReadsOptions>
{
    public ValidateOptionsResult Validate(string? name, ReadsOptions options)
    {
        var failures = new List<string>();

        if (options.IndexTimeout <= TimeSpan.Zero || options.IndexTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add("Reads:IndexTimeout must be positive and at most 30 seconds.");
        }

        var suppliers = contentStore.Value.Suppliers;
        if (suppliers.All(SupplierKey.IsValid))
        {
            failures.AddRange(ReadRouteRules.Check(suppliers, options.Routes, naming.ForSupplier, indexNaming.IndexFor));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
