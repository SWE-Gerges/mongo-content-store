using Content.Mongo.Indexing;
using Content.Store;
using Content.Store.Configuration;
using Content.Sync.DeadLetters;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Sync;

/// <summary>The collections that may be synced: the master plus one per configured supplier, each with the alias its search index is served under.</summary>
public interface ISyncTargetCatalog
{
    IReadOnlyCollection<string> Collections { get; }

    bool IsKnown(string collection);

    /// <summary>The index alias a collection is served under (also the name plain syncs write to).</summary>
    string AliasFor(string collection);

    /// <returns>The collection served under the alias, or null when the alias is not one of this catalog's.</returns>
    string? CollectionForAlias(string alias);
}

internal sealed class SyncTargetCatalog : ISyncTargetCatalog
{
    private readonly HashSet<string> _collections;
    private readonly Dictionary<string, string> _byAlias;
    private readonly SearchIndexNaming _indexNaming;

    public SyncTargetCatalog(ICollectionNaming naming, IOptions<ContentStoreOptions> options, SearchIndexNaming indexNaming)
    {
        _indexNaming = indexNaming;
        _collections = options.Value.Suppliers.Select(naming.ForSupplier).Prepend(naming.Master).ToHashSet(StringComparer.Ordinal);
        _byAlias = _collections.ToDictionary(indexNaming.IndexFor, c => c, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Collections => _collections;

    public bool IsKnown(string collection) => !string.IsNullOrEmpty(collection) && _collections.Contains(collection);

    public string AliasFor(string collection) => IsKnown(collection)
        ? _indexNaming.IndexFor(collection)
        : throw new ArgumentException($"'{collection}' is not a sync target.", nameof(collection));

    public string? CollectionForAlias(string alias) => !string.IsNullOrEmpty(alias) && _byAlias.TryGetValue(alias, out var collection) ? collection : null;
}

/// <summary>Index that keeps dead-letter listings (per collection, newest first) cheap.</summary>
internal sealed class SyncIndexProvider : ICollectionIndexProvider
{
    public IEnumerable<CollectionIndexSet> GetIndexSets()
    {
        yield return new CollectionIndexSet(
            [MongoIndexVersionStore.CollectionName],
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("alias").Ascending("version"),
                    new CreateIndexOptions { Name = "ix_alias_version" }),
            ]);
        yield return new CollectionIndexSet(
            [MongoDeadLetterStore.CollectionName],
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("collection").Descending("firstSeenAt").Ascending("_id"),
                    new CreateIndexOptions { Name = "ix_collection_first_seen_id" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Descending("firstSeenAt").Ascending("_id"),
                    new CreateIndexOptions { Name = "ix_first_seen_id" }),
            ]);
    }
}
