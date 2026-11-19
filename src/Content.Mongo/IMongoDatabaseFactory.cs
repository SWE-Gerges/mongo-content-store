using Content.Mongo.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Content.Mongo;

/// <summary>Hands out database handles from the single shared <see cref="IMongoClient"/>.</summary>
public interface IMongoDatabaseFactory
{
    /// <param name="name">Database name, or <c>null</c> for the configured default.</param>
    IMongoDatabase Get(string? name = null);
}

internal sealed class MongoDatabaseFactory(IMongoClient client, IOptions<MongoOptions> options) : IMongoDatabaseFactory
{
    public IMongoDatabase Get(string? name = null)
    {
        var databaseName = string.IsNullOrWhiteSpace(name) ? options.Value.DefaultDatabase : name;
        return client.GetDatabase(databaseName);
    }
}
