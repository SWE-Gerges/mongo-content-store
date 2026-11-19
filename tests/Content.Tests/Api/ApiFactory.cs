using Content.Mongo.Health;
using Content.Mongo.Repository;
using Content.Store;
using Content.Sync.DeadLetters;
using Content.Sync.Health;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Content.Sync.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Content.Tests.Api;

/// <summary>
/// Hosts the real API pipeline (routing, auth, problem details, OpenAPI, health) with every backing service
/// replaced by a substitute, so contract tests need no database or search cluster.
/// </summary>
internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ReaderKey = "reader-key-for-tests-0001";
    public const string AdminKey = "admin-key-for-tests-0001";

    public IHotelContentStore Store { get; } = Substitute.For<IHotelContentStore>();

    public ISyncCoordinator Coordinator { get; } = Substitute.For<ISyncCoordinator>();

    public IDeadLetterStore DeadLetters { get; } = Substitute.For<IDeadLetterStore>();

    public IMongoHealthProbe MongoProbe { get; } = Substitute.For<IMongoHealthProbe>();

    public ISearchHealthProbe SearchProbe { get; } = Substitute.For<ISearchHealthProbe>();

    /// <summary>The search index as the read path sees it; by default it answers and has no documents, so reads fall through to the store.</summary>
    public ISearchDocumentReader SearchReader { get; } = Substitute.For<ISearchDocumentReader>();

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
    };

    public HttpClient CreateClientWithKey(string? key)
    {
        var client = CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    public ApiFactory()
    {
        SearchReader.GetByIdsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, SearchDocument>());
        SearchReader.GetByCodesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchDocument>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHotelContentStore>();
            services.AddSingleton(Store);
            services.RemoveAll<ISyncCoordinator>();
            services.AddSingleton(Coordinator);
            services.RemoveAll<IDeadLetterStore>();
            services.AddSingleton(DeadLetters);
            services.RemoveAll<IMongoHealthProbe>();
            services.AddSingleton(MongoProbe);
            services.RemoveAll<ISearchDocumentReader>();
            services.AddSingleton(SearchReader);
            services.RemoveAll<ISearchHealthProbe>();
            services.AddSingleton(SearchProbe);
        });
    }
}
