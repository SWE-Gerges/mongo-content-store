using Content.Migration;
using Content.Mongo.Coordination;
using Content.Mongo.Health;
using Content.Reads;
using Content.Store;
using Content.Sync.Checkpoints;
using Content.Sync.DeadLetters;
using Content.Sync.Health;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Content.Sync.Source;
using Content.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Content.Tests.Api;

/// <summary>
/// Hosts the real API with its real coordinators, runner and workers over the in-memory stores of a
/// <see cref="FakeWorld"/>. Only the ports that would reach MongoDB or OpenSearch are replaced, so a flow driven
/// over HTTP exercises the same code the deployed service runs.
/// </summary>
internal sealed class FakeBackedApiFactory(FakeWorld world) : WebApplicationFactory<Program>
{
    public const string ReaderKey = "reader-key-for-tests-0001";
    public const string AdminKey = "admin-key-for-tests-0001";

    public FakeWorld World { get; } = world;

    public IHotelContentStore Store { get; } = Substitute.For<IHotelContentStore>();

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Mongo:ConnectionString"] = "mongodb://localhost:27017",
        ["Mongo:DefaultDatabase"] = "content_tests",
        ["Mongo:BootstrapIndexes"] = "false",
        ["OpenSearch:Uri"] = "http://localhost:9200",
        ["Auth:ApiKeys:0"] = ReaderKey,
        ["Auth:AdminKeys:0"] = AdminKey,
        ["ContentStore:Suppliers:0"] = "supplier-a",
        ["ContentStore:Suppliers:1"] = "supplier-b",
        ["Sync:BatchSize"] = "50",
        ["Sync:ReadBackoffBase"] = "00:00:00",
        ["Sync:BulkBackoffBase"] = "00:00:00",
        ["Sync:ItemRetryDelay"] = "00:00:00",
        ["Coordination:LeaseDuration"] = "00:00:02",
        ["Coordination:HeartbeatInterval"] = "00:00:00.050",
    };

    /// <summary>Adds a migration definition to this host's configuration.</summary>
    public FakeBackedApiFactory WithMigration(MigrationDefinition definition, int index = 0)
    {
        var prefix = $"Migration:Definitions:{index}";
        Settings[$"{prefix}:Name"] = definition.Name;
        Settings[$"{prefix}:Provider"] = definition.Provider;
        Settings[$"{prefix}:ConnectionString"] = definition.ConnectionString;
        Settings[$"{prefix}:Query"] = definition.Query;
        Settings[$"{prefix}:KeyColumn"] = definition.KeyColumn;
        Settings[$"{prefix}:KeyType"] = definition.KeyType.ToString();
        Settings[$"{prefix}:TargetCollection"] = definition.TargetCollection;
        Settings[$"{prefix}:BatchSize"] = definition.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < definition.Columns.Count; i++)
        {
            var column = definition.Columns[i];
            Settings[$"{prefix}:Columns:{i}:Column"] = column.Column;
            Settings[$"{prefix}:Columns:{i}:Field"] = column.Field;
            Settings[$"{prefix}:Columns:{i}:Type"] = column.Type.ToString();
            Settings[$"{prefix}:Columns:{i}:Required"] = column.Required ? "true" : "false";
        }

        return this;
    }

    public HttpClient CreateClientWithKey(string key)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            Replace<IHotelContentStore>(services, Store);
            Replace<ISourceReader>(services, World.Source);
            Replace<ISearchIndexWriter>(services, World.Index);
            Replace<ISearchIndexAdmin>(services, World.Index);
            Replace<ISearchDocumentReader>(services, World.Index);
            Replace<ISourceStatistics>(services, World.Source);
            Replace<IIndexVersionStore>(services, World.Versions);
            Replace<IIndexMissSink>(services, World.Misses);
            Replace<IMigrationTarget>(services, World.MigrationTarget);
            Replace<ICheckpointStore>(services, World.Checkpoints);
            Replace<IDeadLetterStore>(services, World.DeadLetters);
            Replace<ILeaseStore>(services, World.Leases);
            Replace<IRunStore>(services, World.Runs);
            Replace<IMongoHealthProbe>(services, Substitute.For<IMongoHealthProbe>());
            Replace<ISearchHealthProbe>(services, Substitute.For<ISearchHealthProbe>());
            Replace<TimeProvider>(services, World.Clock);
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }
}
