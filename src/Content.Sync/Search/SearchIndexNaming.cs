using Content.Sync.Configuration;
using Microsoft.Extensions.Options;

namespace Content.Sync.Search;

/// <summary>Names the search index that mirrors a Mongo collection.</summary>
public sealed class SearchIndexNaming(IOptions<SearchOptions> options)
{
    public string IndexFor(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        return options.Value.IndexPrefix + collection.ToLowerInvariant();
    }
}
