using Content.Mongo.Configuration;
using Content.Mongo.Coordination;
using Content.Mongo.Health;
using Content.Mongo.Indexing;
using Content.Mongo.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Content.Mongo;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared Mongo client (one singleton), the database factory, the generic repository,
    /// the health probe and the index bootstrapper. Options are validated when the host starts.
    /// </summary>
    public static IServiceCollection AddContentMongo(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MongoOptions>, MongoOptionsValidator>());

        services.TryAddSingleton<IMongoClient>(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
            var settings = MongoClientSettings.FromUrl(MongoUrl.Create(options.ConnectionString));
            settings.ConnectTimeout = options.ConnectTimeout;
            settings.ServerSelectionTimeout = options.ServerSelectionTimeout;
            settings.MaxConnectionPoolSize = options.MaxConnectionPoolSize;
            return new MongoClient(settings);
        });

        services.TryAddSingleton<IMongoDatabaseFactory, MongoDatabaseFactory>();
        services.TryAddSingleton(typeof(IMongoRepository<>), typeof(MongoRepository<>));
        services.TryAddSingleton<IMongoHealthProbe, MongoHealthProbe>();
        services.TryAddSingleton<IndexBootstrapper>();
        services.AddHostedService(provider => provider.GetRequiredService<IndexBootstrapper>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<CoordinationOptions>().Bind(configuration.GetSection(CoordinationOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CoordinationOptions>, CoordinationOptionsValidator>());
        services.TryAddSingleton<InstanceIdentity>();
        services.TryAddSingleton<ILeaseStore, MongoLeaseStore>();
        services.TryAddSingleton<IRunStore, MongoRunStore>();
        services.TryAddSingleton<RunCoordinator>();
        services.AddHostedService<RunHost>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectionIndexProvider, CoordinationIndexProvider>());
        return services;
    }
}
