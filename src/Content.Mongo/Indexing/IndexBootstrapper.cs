using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Content.Mongo.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Polly;
using Polly.Retry;

namespace Content.Mongo.Indexing;

/// <summary>
/// Ensures every declared index exists before the application serves traffic. Creating an index that already
/// exists with the same definition is a no-op on the server, so running this on every start is safe.
/// </summary>
public sealed class IndexBootstrapper(
    IMongoDatabaseFactory databases,
    IEnumerable<ICollectionIndexProvider> providers,
    IOptions<MongoOptions> options,
    ILogger<IndexBootstrapper> logger) : IHostedService
{
    private static readonly ResiliencePipeline Retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<MongoConnectionException>().Handle<TimeoutException>(),
            MaxRetryAttempts = 4,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        })
        .Build();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BootstrapIndexes)
        {
            logger.LogInformation("Index bootstrapping is disabled.");
            return;
        }

        await Retry.ExecuteAsync(async token => await EnsureIndexesAsync(token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates all declared indexes. Safe to call repeatedly.</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        foreach (var set in providers.SelectMany(provider => provider.GetIndexSets()))
        {
            var database = databases.Get(set.Database);
            foreach (var collectionName in set.Collections)
            {
                var collection = database.GetCollection<BsonDocument>(collectionName);
                var created = await collection.Indexes.CreateManyAsync(set.Indexes, cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    "Ensured {IndexCount} indexes on {Collection}: {Indexes}",
                    set.Indexes.Count,
                    collectionName,
                    string.Join(", ", created));
            }
        }
    }
}
