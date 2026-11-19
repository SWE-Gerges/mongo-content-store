using Content.Sync.Search;
using OpenSearch.Client;

namespace Content.Sync.Indexing;

/// <summary>
/// Index administration over the OpenSearch client. The alias swap is one <c>_aliases</c> request whose action list
/// removes the old targets and adds the new one, which the cluster applies atomically.
/// </summary>
internal sealed class OpenSearchIndexAdmin(IOpenSearchClient client) : ISearchIndexAdmin
{
    public async Task<IReadOnlyList<string>> ListIndexesAsync(string prefix, CancellationToken cancellationToken)
    {
        var response = await client.Cat.IndicesAsync(c => c.Index(prefix + "*"), cancellationToken).ConfigureAwait(false);
        if (!response.IsValid && response.ApiCall.HttpStatusCode is not 404)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"listing indexes named '{prefix}*'", cancellationToken, response.ServerError?.Error?.Reason);
        }

        return response.Records.Select(r => r.Index).Where(name => !string.IsNullOrEmpty(name)).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<AliasState> GetAliasAsync(string name, CancellationToken cancellationToken)
    {
        var alias = await client.Indices.GetAliasAsync(Indices.All, a => a.Name(name), cancellationToken).ConfigureAwait(false);
        if (alias.IsValid && alias.Indices.Count > 0)
        {
            return new AliasState(name, true, false, alias.Indices.Keys.Select(k => k.Name).Order(StringComparer.Ordinal).ToList());
        }

        if (!alias.IsValid && alias.ApiCall.HttpStatusCode is not 404)
        {
            throw OpenSearchErrors.Translate(alias.ApiCall.HttpStatusCode, alias.OriginalException, $"reading alias '{name}'", cancellationToken, alias.ServerError?.Error?.Reason);
        }

        // An alias lookup does not find a plain index of the same name, so ask for the index itself.
        return await IndexExistsAsync(name, cancellationToken).ConfigureAwait(false)
            ? new AliasState(name, true, true, [name])
            : AliasState.Missing(name);
    }

    public async Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken)
    {
        var response = await client.Indices.ExistsAsync(index, ct: cancellationToken).ConfigureAwait(false);
        if (!response.ApiCall.Success && response.ApiCall.HttpStatusCode is not 404)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"checking whether '{index}' exists", cancellationToken);
        }

        return response.Exists;
    }

    public async Task RefreshAsync(string index, CancellationToken cancellationToken)
    {
        var response = await client.Indices.RefreshAsync(index, ct: cancellationToken).ConfigureAwait(false);
        if (!response.IsValid)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"refreshing '{index}'", cancellationToken, response.ServerError?.Error?.Reason);
        }
    }

    public async Task<long> CountAsync(string index, CancellationToken cancellationToken)
    {
        var response = await client.CountAsync<SearchDocument>(c => c.Index(index), cancellationToken).ConfigureAwait(false);
        if (!response.IsValid)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"counting '{index}'", cancellationToken, response.ServerError?.Error?.Reason);
        }

        return response.Count;
    }

    public async Task SwapAliasAsync(AliasSwap swap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(swap);
        var response = await client.Indices
            .BulkAliasAsync(
                bulk =>
                {
                    foreach (var index in swap.RemoveIndexes)
                    {
                        _ = swap.ReplaceConcreteIndex && index == swap.Alias
                            ? bulk.RemoveIndex(r => r.Index(index))
                            : bulk.Remove(r => r.Index(index).Alias(swap.Alias));
                    }

                    return bulk.Add(a => a.Index(swap.AddIndex).Alias(swap.Alias));
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsValid)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"switching alias '{swap.Alias}' to '{swap.AddIndex}'", cancellationToken, response.ServerError?.Error?.Reason);
        }
    }

    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken)
    {
        var response = await client.Indices.DeleteAsync(index, ct: cancellationToken).ConfigureAwait(false);
        if (!response.IsValid && response.ApiCall.HttpStatusCode is not 404)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"deleting index '{index}'", cancellationToken, response.ServerError?.Error?.Reason);
        }
    }
}

internal sealed class OpenSearchDocumentReader(IOpenSearchClient client) : ISearchDocumentReader
{
    public async Task<IReadOnlyDictionary<string, SearchDocument>> GetByIdsAsync(string indexOrAlias, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, SearchDocument>();
        }

        var response = await client
            .MultiGetAsync(m => m.Index(indexOrAlias).GetMany<SearchDocument>(ids), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsValid)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"reading documents from '{indexOrAlias}'", cancellationToken, response.ServerError?.Error?.Reason);
        }

        return response.GetMany<SearchDocument>(ids)
            .Where(hit => hit.Found && hit.Source is not null)
            .ToDictionary(hit => hit.Id, hit => hit.Source, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<SearchDocument>> GetByCodesAsync(string indexOrAlias, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var response = await client
            .SearchAsync<SearchDocument>(
                s => s.Index(indexOrAlias).Size(codes.Count).Query(q => q.Terms(t => t.Field(d => d.Code).Terms(codes))),
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsValid)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"searching '{indexOrAlias}' by code", cancellationToken, response.ServerError?.Error?.Reason);
        }

        return response.Documents.ToList();
    }
}
