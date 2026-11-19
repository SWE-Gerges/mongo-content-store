using Content.Mongo;
using Content.Mongo.Configuration;
using Content.Mongo.Indexing;
using Content.Mongo.Repository;
using Content.Store;
using Content.Store.Configuration;
using Content.Store.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Tests.Mongo;

public class MongoOptionsValidatorTests
{
    private readonly MongoOptionsValidator _validator = new();

    private static MongoOptions Valid() => new() { ConnectionString = "mongodb://localhost:27017", DefaultDatabase = "content" };

    [Fact]
    public void Accepts_valid_options_with_the_documented_defaults()
    {
        var options = Valid();

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
        options.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        options.ServerSelectionTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        options.MaxConnectionPoolSize.ShouldBe(100);
    }

    [Fact]
    public void Offers_no_socket_timeout_setting() =>
        typeof(MongoOptions).GetProperties().Select(p => p.Name).ShouldNotContain(name => name.Contains("Socket", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Rejects_a_connection_string_that_sets_a_socket_timeout()
    {
        var options = Valid();
        options.ConnectionString = "mongodb://localhost:27017/?socketTimeoutMS=600000";

        _validator.Validate(null, options).Failures!.ShouldHaveSingleItem().ShouldContain("socketTimeoutMS");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a mongo url")]
    [InlineData("http://localhost")]
    public void Rejects_missing_or_malformed_connection_strings(string connectionString)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    public void Rejects_invalid_database_names(string database)
    {
        var options = Valid();
        options.DefaultDatabase = database;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Rejects_non_positive_timeouts_and_pool_sizes()
    {
        var options = Valid();
        options.ConnectTimeout = TimeSpan.Zero;
        options.ServerSelectionTimeout = TimeSpan.FromSeconds(-1);
        options.MaxConnectionPoolSize = 0;

        _validator.Validate(null, options).Failures!.Count().ShouldBe(3);
    }
}

public class ContentStoreOptionsValidatorTests
{
    private readonly ContentStoreOptionsValidator _validator = new();

    [Fact]
    public void Accepts_configured_suppliers() =>
        _validator.Validate(null, new ContentStoreOptions { Suppliers = ["supplier-a", "supplier-b"] }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Requires_at_least_one_supplier() =>
        _validator.Validate(null, new ContentStoreOptions()).Failed.ShouldBeTrue();

    [Fact]
    public void Rejects_invalid_and_duplicate_supplier_keys()
    {
        _validator.Validate(null, new ContentStoreOptions { Suppliers = ["Supplier A"] }).Failed.ShouldBeTrue();
        _validator.Validate(null, new ContentStoreOptions { Suppliers = ["supplier-a", "supplier-a"] }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Rejects_a_default_page_size_above_the_maximum() =>
        _validator.Validate(null, new ContentStoreOptions { Suppliers = ["supplier-a"], DefaultPageSize = 500, MaxPageSize = 100 }).Failed.ShouldBeTrue();
}

public class HotelIndexProviderTests
{
    private static List<CollectionIndexSet> Sets(params string[] suppliers) =>
        new HotelIndexProvider(new HotelCollectionNaming(), Options.Create(new ContentStoreOptions { Suppliers = [.. suppliers] }))
            .GetIndexSets()
            .ToList();

    [Fact]
    public void Declares_the_indexes_for_the_master_and_every_configured_supplier_collection()
    {
        var common = Sets("supplier-a", "supplier-b")[0];

        common.Collections.ShouldBe(["hotels", "hotel_supplier-a", "hotel_supplier-b"]);
        common.Indexes.Select(i => i.Options.Name).ShouldBe(
            ["ix_code", "ix_city_id", "ix_country_name", "ix_name", "ix_name_city_ci"]);
    }

    [Fact]
    public void A_new_supplier_is_picked_up_from_configuration_alone() =>
        Sets("supplier-a", "supplier-new")[0].Collections.ShouldContain("hotel_supplier-new");

    [Fact]
    public void The_case_insensitive_index_carries_an_en_strength_2_collation()
    {
        var index = Sets("supplier-a")[0].Indexes.Single(i => i.Options.Name == "ix_name_city_ci");

        index.Options.Collation!.Locale.ShouldBe("en");
        index.Options.Collation.Strength.ShouldBe(CollationStrength.Secondary);
        index.Keys.Render(new RenderArgs<BsonDocument>(BsonDocumentSerializer(), MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry))
            .ShouldBe(new BsonDocument { { "Name", 1 }, { "CityCode", 1 } });
    }

    [Fact]
    public void Only_the_master_gets_the_provider_index()
    {
        var sets = Sets("supplier-a");

        sets[1].Collections.ShouldBe(["hotels"]);
        sets[1].Indexes.Single().Options.Name.ShouldBe("ix_providers");
    }

    private static MongoDB.Bson.Serialization.IBsonSerializer<BsonDocument> BsonDocumentSerializer() =>
        MongoDB.Bson.Serialization.BsonSerializer.LookupSerializer<BsonDocument>();
}

public class ServiceRegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static readonly Dictionary<string, string?> ValidSettings = new()
    {
        ["Mongo:ConnectionString"] = "mongodb://localhost:27017",
        ["Mongo:DefaultDatabase"] = "content",
        ["ContentStore:Suppliers:0"] = "supplier-a",
        ["ContentStore:Suppliers:1"] = "supplier-b",
    };

    [Fact]
    public void Resolves_the_graph_with_one_shared_client_and_the_generic_repository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentMongo(Config(ValidSettings)).AddContentStore(Config(ValidSettings));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<IMongoClient>().ShouldBeSameAs(provider.GetRequiredService<IMongoClient>());
        provider.GetRequiredService<IMongoRepository<HotelDocument>>().ShouldNotBeNull();
        provider.GetRequiredService<IMongoRepository<BsonDocument>>().ShouldNotBeNull();
        provider.GetRequiredService<IHotelContentStore>().ShouldNotBeNull();
        provider.GetRequiredService<IMongoDatabaseFactory>().Get().DatabaseNamespace.DatabaseName.ShouldBe("content");
        provider.GetRequiredService<IMongoDatabaseFactory>().Get("other").DatabaseNamespace.DatabaseName.ShouldBe("other");
    }

    [Fact]
    public void Applies_the_connection_settings_without_a_socket_timeout()
    {
        var services = new ServiceCollection();
        services.AddContentMongo(Config(ValidSettings));
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<IMongoClient>().Settings;

        settings.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        settings.ServerSelectionTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        settings.MaxConnectionPoolSize.ShouldBe(100);
        settings.SocketTimeout.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Invalid_options_fail_when_first_read()
    {
        var services = new ServiceCollection();
        services.AddContentMongo(Config(new Dictionary<string, string?> { ["Mongo:ConnectionString"] = "" }));
        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MongoOptions>>().Value);
    }
}
