using Content.Mongo.Coordination;
using Content.Mongo.Indexing;
using Content.Sync.Checkpoints;
using Content.Sync.Configuration;
using Content.Sync.DeadLetters;
using Content.Sync.Health;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Content.Sync.Source;
using Content.Sync.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenSearch.Client;

namespace Content.Sync;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Mongo to OpenSearch sync: client, writer, bulk indexer, checkpoint and dead-letter stores, the
    /// runner, and the sync worker that executes runs started through <see cref="ISyncCoordinator"/> (the run host itself comes
    /// with <c>AddContentMongo</c>).
    /// Requires <c>AddContentMongo</c> and <c>AddContentStore</c>.
    /// </summary>
    public static IServiceCollection AddContentSync(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOpenSearchClient>(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<SearchOptions>>().Value;
            var settings = new ConnectionSettings(new Uri(options.Uri)).RequestTimeout(options.RequestTimeout);
            if (!string.IsNullOrEmpty(options.Username))
            {
                settings.BasicAuthentication(options.Username, options.Password);
            }

            return new OpenSearchClient(settings);
        });

        services.TryAddSingleton<SearchIndexNaming>();
        services.TryAddSingleton<ISearchIndexWriter, OpenSearchIndexWriter>();
        services.TryAddSingleton<OpenSearchBulkIndexer>();
        services.TryAddSingleton<ISearchHealthProbe, OpenSearchHealthProbe>();
        services.TryAddSingleton<ISourceReader, MongoSourceReader>();
        services.TryAddSingleton<ICheckpointStore, MongoCheckpointStore>();
        services.TryAddSingleton<IDeadLetterStore, MongoDeadLetterStore>();
        services.TryAddSingleton<ISyncTargetCatalog, SyncTargetCatalog>();
        services.TryAddSingleton<SyncRunner>();
        services.TryAddSingleton<ISyncCoordinator, SyncCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunWorker, SyncRunWorker>());
        services.AddOptions<IndexVersioningOptions>().Bind(configuration.GetSection(IndexVersioningOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IndexVersioningOptions>, IndexVersioningOptionsValidator>());
        services.TryAddSingleton<ISearchIndexAdmin, OpenSearchIndexAdmin>();
        services.TryAddSingleton<ISearchDocumentReader, OpenSearchDocumentReader>();
        services.TryAddSingleton<ISourceStatistics, MongoSourceStatistics>();
        services.TryAddSingleton<IIndexVersionStore, MongoIndexVersionStore>();
        services.TryAddSingleton<IndexVerifier>();
        services.TryAddSingleton<IndexVersionManager>();
        services.TryAddSingleton<IIndexVersioning, IndexVersioningService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunWorker, IndexRebuildWorker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectionIndexProvider, SyncIndexProvider>());
        return services;
    }
}
