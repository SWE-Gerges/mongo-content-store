using Content.Sync.Indexing;
using Content.Sync.Search;

namespace Content.TestSupport;

/// <summary>
/// A search cluster in memory, as strict as the real one where it matters to this code: an alias cannot take the name of
/// an existing index (unless the same request removes that index), a bulk write needs exactly one target behind an alias,
/// a missing index is an error, and an alias change is applied as one step under a lock.
/// </summary>
public sealed class InMemorySearchCluster : ISearchIndexWriter, ISearchIndexAdmin, ISearchDocumentReader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, SearchDocument>> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _aliases = new(StringComparer.Ordinal);
    private readonly List<AliasSwap> _swaps = [];
    private int _indexOperations;

    /// <summary>Given an operation name (<c>ensure</c>, <c>bulk</c>, <c>read</c>, <c>admin</c>), returns an exception to throw instead of doing it.</summary>
    public Func<string, Exception?>? FailWhen { get; set; }

    /// <summary>Runs before every read, so a test can make the cluster slow or block it until released.</summary>
    public Func<CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Documents for which a bulk write reports success but stores nothing, as a lossy cluster would.</summary>
    public Func<SearchDocument, bool>? SilentlyDropWhen { get; set; }

    /// <summary>Changes a document on its way into the index, as a faulty pipeline would.</summary>
    public Func<SearchDocument, SearchDocument>? CorruptOnWrite { get; set; }

    public int IndexOperations => Volatile.Read(ref _indexOperations);

    public int Reads { get; private set; }

    public IReadOnlyList<AliasSwap> Swaps
    {
        get
        {
            lock (_gate)
            {
                return [.. _swaps];
            }
        }
    }

    public IReadOnlyCollection<string> IndexNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _indexes.Keys];
            }
        }
    }

    public IReadOnlyCollection<string> AliasTargets(string alias)
    {
        lock (_gate)
        {
            return _aliases.TryGetValue(alias, out var targets) ? [.. targets.Order(StringComparer.Ordinal)] : [];
        }
    }

    /// <summary>Ids held by an index, or by every index behind an alias.</summary>
    public IReadOnlyCollection<string> Ids(string indexOrAlias)
    {
        lock (_gate)
        {
            return [.. Resolve(indexOrAlias).SelectMany(name => _indexes[name].Keys)];
        }
    }

    public IReadOnlyDictionary<string, SearchDocument> Documents(string indexOrAlias)
    {
        lock (_gate)
        {
            return Resolve(indexOrAlias).SelectMany(name => _indexes[name]).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>Creates an index (and optionally an alias for it) outside any code under test.</summary>
    public void Seed(string index, IEnumerable<SearchDocument> documents, string? alias = null)
    {
        lock (_gate)
        {
            _indexes[index] = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
            if (alias is not null)
            {
                if (!_aliases.TryGetValue(alias, out var targets))
                {
                    _aliases[alias] = targets = new HashSet<string>(StringComparer.Ordinal);
                }

                targets.Add(index);
            }
        }
    }

    public Task EnsureIndexAsync(string indexName, CancellationToken cancellationToken)
    {
        Fail("ensure");
        lock (_gate)
        {
            if (!_indexes.ContainsKey(indexName) && !_aliases.ContainsKey(indexName))
            {
                _indexes[indexName] = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
            }
        }

        return Task.CompletedTask;
    }

    public Task<BulkWriteOutcome> BulkIndexAsync(string indexName, IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken)
    {
        Fail("bulk");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            string target;
            if (_indexes.ContainsKey(indexName))
            {
                target = indexName;
            }
            else if (_aliases.TryGetValue(indexName, out var targets))
            {
                target = targets.Count == 1
                    ? targets.Single()
                    : throw new SearchIndexException($"no write index is defined for alias '{indexName}' ({targets.Count} indexes behind it)");
            }
            else
            {
                throw new SearchIndexException($"index_not_found_exception: {indexName}");
            }

            foreach (var document in documents)
            {
                if (SilentlyDropWhen?.Invoke(document) != true)
                {
                    _indexes[target][document.Id] = CorruptOnWrite?.Invoke(document) ?? document;
                }

                Interlocked.Increment(ref _indexOperations);
            }
        }

        return Task.FromResult(BulkWriteOutcome.AllIndexed(documents.Count));
    }

    public Task<IReadOnlyList<string>> ListIndexesAsync(string prefix, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _indexes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
        }
    }

    public Task<AliasState> GetAliasAsync(string name, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            if (_aliases.TryGetValue(name, out var targets))
            {
                return Task.FromResult(new AliasState(name, true, false, [.. targets.Order(StringComparer.Ordinal)]));
            }

            return Task.FromResult(_indexes.ContainsKey(name) ? new AliasState(name, true, true, [name]) : AliasState.Missing(name));
        }
    }

    public Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            return Task.FromResult(_indexes.ContainsKey(index) || _aliases.ContainsKey(index));
        }
    }

    public Task RefreshAsync(string index, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            _ = Resolve(index);
        }

        return Task.CompletedTask;
    }

    public Task<long> CountAsync(string index, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            return Task.FromResult((long)Resolve(index).Sum(name => _indexes[name].Count));
        }
    }

    public Task SwapAliasAsync(AliasSwap swap, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            if (!_indexes.ContainsKey(swap.AddIndex))
            {
                throw new SearchIndexException($"index_not_found_exception: {swap.AddIndex}");
            }

            var aliasIsConcrete = _indexes.ContainsKey(swap.Alias);
            if (aliasIsConcrete && !(swap.ReplaceConcreteIndex && swap.RemoveIndexes.Contains(swap.Alias)))
            {
                throw new SearchIndexException($"invalid_alias_name_exception: an index exists with the same name as the alias [{swap.Alias}]");
            }

            _aliases.TryGetValue(swap.Alias, out var targets);
            foreach (var removed in swap.RemoveIndexes.Where(r => !(aliasIsConcrete && r == swap.Alias)))
            {
                if (targets is null || !targets.Contains(removed))
                {
                    throw new SearchIndexException($"aliases_not_found_exception: alias [{swap.Alias}] does not point at [{removed}]");
                }
            }

            if (aliasIsConcrete)
            {
                _indexes.Remove(swap.Alias);
            }

            foreach (var removed in swap.RemoveIndexes.Where(r => !(aliasIsConcrete && r == swap.Alias)))
            {
                targets!.Remove(removed);
            }

            if (targets is null)
            {
                _aliases[swap.Alias] = targets = new HashSet<string>(StringComparer.Ordinal);
            }

            targets.Add(swap.AddIndex);
            _swaps.Add(swap);
        }

        return Task.CompletedTask;
    }

    public Task DeleteIndexAsync(string index, CancellationToken cancellationToken)
    {
        Fail("admin");
        lock (_gate)
        {
            _indexes.Remove(index);
            foreach (var targets in _aliases.Values)
            {
                targets.Remove(index);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, SearchDocument>> GetByIdsAsync(string indexOrAlias, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        await BeforeReadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var documents = Resolve(indexOrAlias).Select(name => _indexes[name]).ToList();
            return ids
                .Select(id => documents.Select(d => d.GetValueOrDefault(id)).FirstOrDefault(d => d is not null))
                .OfType<SearchDocument>()
                .ToDictionary(d => d.Id, StringComparer.Ordinal);
        }
    }

    public async Task<IReadOnlyList<SearchDocument>> GetByCodesAsync(string indexOrAlias, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        await BeforeReadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return [.. Resolve(indexOrAlias).SelectMany(name => _indexes[name].Values).Where(d => d.Code is not null && codes.Contains(d.Code))];
        }
    }

    private async Task BeforeReadAsync(CancellationToken cancellationToken)
    {
        Fail("read");
        lock (_gate)
        {
            Reads++;
        }

        if (BeforeRead is { } before)
        {
            await before(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Fail(string operation)
    {
        if (FailWhen?.Invoke(operation) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Physical indexes behind a name. Callers hold the lock.</summary>
    private List<string> Resolve(string name)
    {
        if (_indexes.ContainsKey(name))
        {
            return [name];
        }

        return _aliases.TryGetValue(name, out var targets) && targets.Count > 0
            ? [.. targets]
            : throw new SearchIndexException($"index_not_found_exception: {name}");
    }
}
