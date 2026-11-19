using Content.Mongo.Indexing;
using Content.Mongo.Serialization;
using Content.Store.Configuration;
using Content.Store.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Content.Store;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tolerant BSON mappings, the content store, the room mapper registry and the index
    /// declarations. Call <c>AddContentMongo</c> as well; it supplies the repository and index bootstrapper.
    /// </summary>
    public static IServiceCollection AddContentStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ContentStoreOptions>()
            .Bind(configuration.GetSection(ContentStoreOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ContentStoreOptions>, ContentStoreOptionsValidator>());

        var sink = HotelBsonMappings.EnsureRegistered(new MalformedFieldCounter());
        services.TryAddSingleton(sink);
        if (sink is MalformedFieldCounter counter)
        {
            services.TryAddSingleton(counter);
        }

        services.TryAddSingleton<ICollectionNaming, HotelCollectionNaming>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRoomExtraMapper, SupplierARoomExtraMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRoomExtraMapper, SupplierBRoomExtraMapper>());
        services.TryAddSingleton<RoomExtraMapperRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectionIndexProvider, HotelIndexProvider>());
        services.TryAddSingleton<IHotelContentStore, HotelContentStore>();
        return services;
    }
}
