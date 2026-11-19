using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Indexing;

/// <summary>A group of indexes that every collection in <see cref="Collections"/> must have.</summary>
/// <param name="Collections">Collection names the indexes are ensured on.</param>
/// <param name="Indexes">Index definitions; each must carry an explicit name so ensuring is idempotent.</param>
/// <param name="Database">Database name, or null for the configured default.</param>
public sealed record CollectionIndexSet(
    IReadOnlyCollection<string> Collections,
    IReadOnlyList<CreateIndexModel<BsonDocument>> Indexes,
    string? Database = null);

/// <summary>
/// Declares the indexes a feature depends on, so nothing relies on hand-created indexes.
/// Implementations are evaluated lazily at startup so supplier lists can come from configuration.
/// </summary>
public interface ICollectionIndexProvider
{
    IEnumerable<CollectionIndexSet> GetIndexSets();
}
