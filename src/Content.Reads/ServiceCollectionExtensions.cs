using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Content.Reads;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the search-index-first read path and its route table. The table is validated when the host starts.
    /// Requires <c>AddContentMongo</c>, <c>AddContentStore</c> and <c>AddContentSync</c>.
    /// </summary>
    public static IServiceCollection AddContentReads(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ReadsOptions>().Bind(configuration.GetSection(ReadsOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ReadsOptions>, ReadsOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReadRouteTable>();
        services.TryAddSingleton<IIndexMissSink, MetricsIndexMissSink>();
        services.TryAddSingleton<IContentReader, ContentReader>();
        return services;
    }
}
