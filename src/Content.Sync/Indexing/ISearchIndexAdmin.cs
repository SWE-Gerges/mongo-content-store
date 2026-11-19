using Content.Sync.Search;

namespace Content.Sync.Indexing;

/// <summary>What a name currently is on the cluster: nothing, a concrete index, or an alias over some indexes.</summary>
/// <param name="IsConcreteIndex">The name belongs to a real index rather than an alias (an index from before versioning).</param>
/// <param name="Targets">The indexes an alias points at; for a concrete index, the index itself.</param>
public sealed record AliasState(string Name, bool Exists, bool IsConcreteIndex, IReadOnlyList<string> Targets)
{
    public static AliasState Missing(string name) => new(name, false, false, []);
}

/// <summary>
/// One alias change. Everything in it is applied by the cluster in a single request, so readers see the alias on the
/// old index or on the new one and never without either.
/// </summary>
/// <param name="AddIndex">The index the alias should point at afterwards.</param>
/// <param name="RemoveIndexes">Indexes the alias stops pointing at.</param>
/// <param name="ReplaceConcreteIndex">
/// The alias name is currently a concrete index (from before versioning). The same request deletes that index and
/// creates the alias in its place, which is the only way to give an index's name to an alias without a gap.
/// </param>
public sealed record AliasSwap(string Alias, string AddIndex, IReadOnlyList<string> RemoveIndexes, bool ReplaceConcreteIndex);

/// <summary>Cluster operations needed to build, verify, switch and prune physical indexes behind an alias.</summary>
public interface ISearchIndexAdmin
{
    /// <summary>Names of the physical indexes whose name starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<string>> ListIndexesAsync(string prefix, CancellationToken cancellationToken);

    Task<AliasState> GetAliasAsync(string name, CancellationToken cancellationToken);

    Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken);

    /// <summary>Makes everything indexed so far visible to counts and reads.</summary>
    Task RefreshAsync(string index, CancellationToken cancellationToken);

    Task<long> CountAsync(string index, CancellationToken cancellationToken);

    /// <summary>Applies the swap as one request.</summary>
    Task SwapAliasAsync(AliasSwap swap, CancellationToken cancellationToken);

    Task DeleteIndexAsync(string index, CancellationToken cancellationToken);
}

/// <summary>Reads search documents back by key. Throws <see cref="SearchIndexUnavailableException"/> when the cluster does not answer.</summary>
public interface ISearchDocumentReader
{
    /// <returns>The documents that exist, keyed by id. Ids that are not in the index are simply absent.</returns>
    Task<IReadOnlyDictionary<string, SearchDocument>> GetByIdsAsync(string indexOrAlias, IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <returns>The documents whose <c>Code</c> is one of the codes.</returns>
    Task<IReadOnlyList<SearchDocument>> GetByCodesAsync(string indexOrAlias, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
}
