using Content.Mongo.Indexing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Coordination;

/// <summary>Keeps "latest run for a target" cheap.</summary>
internal sealed class CoordinationIndexProvider : ICollectionIndexProvider
{
    public IEnumerable<CollectionIndexSet> GetIndexSets()
    {
        yield return new CollectionIndexSet(
            [MongoRunStore.CollectionName],
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("target").Descending("requestedAt"),
                    new CreateIndexOptions { Name = "ix_target_requested" }),
            ]);
    }
}
