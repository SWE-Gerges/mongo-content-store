using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Health;

/// <summary>Checks that the MongoDB deployment answers. Throws when it does not.</summary>
public interface IMongoHealthProbe
{
    Task PingAsync(CancellationToken cancellationToken);
}

internal sealed class MongoHealthProbe(IMongoDatabaseFactory databases) : IMongoHealthProbe
{
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        var command = new BsonDocumentCommand<BsonDocument>(new BsonDocument("ping", 1));
        await databases.Get().RunCommandAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
