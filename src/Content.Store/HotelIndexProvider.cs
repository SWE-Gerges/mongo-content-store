using Content.Mongo.Indexing;
using Content.Store.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Store;

/// <summary>
/// Declares the indexes the store's queries rely on, for the master collection and every configured supplier
/// collection, so nothing depends on hand-created indexes.
/// </summary>
internal sealed class HotelIndexProvider(ICollectionNaming naming, IOptions<ContentStoreOptions> options) : ICollectionIndexProvider
{
    public const string CodeIndex = "ix_code";
    public const string CityIndex = "ix_city_id";
    public const string CountryNameIndex = "ix_country_name";
    public const string NameIndex = "ix_name";
    public const string NameCityCaseInsensitiveIndex = "ix_name_city_ci";
    public const string ProvidersIndex = "ix_providers";

    private static IndexKeysDefinitionBuilder<BsonDocument> Keys => Builders<BsonDocument>.IndexKeys;

    public IEnumerable<CollectionIndexSet> GetIndexSets()
    {
        var collections = options.Value.Suppliers
            .Select(naming.ForSupplier)
            .Prepend(naming.Master)
            .ToList();

        yield return new CollectionIndexSet(collections, CommonIndexes());
        yield return new CollectionIndexSet(
            [naming.Master],
            [
                new CreateIndexModel<BsonDocument>(
                    Keys.Ascending("Providers.Supplier").Ascending("Providers.HotelId"),
                    new CreateIndexOptions { Name = ProvidersIndex }),
            ]);
    }

    private static IReadOnlyList<CreateIndexModel<BsonDocument>> CommonIndexes() =>
    [
        new(Keys.Ascending("Code"), new CreateIndexOptions { Name = CodeIndex }),

        // _id rides along so a city page (CityCode equality, _id range, _id order) is a covered index scan.
        new(Keys.Ascending("CityCode").Ascending("_id"), new CreateIndexOptions { Name = CityIndex }),
        new(Keys.Ascending("CountryCode").Ascending("Name"), new CreateIndexOptions { Name = CountryNameIndex }),
        new(Keys.Ascending("Name"), new CreateIndexOptions { Name = NameIndex }),

        // Strength 2 ignores case and keeps accents, matching what "case-insensitive name" means to users.
        new(
            Keys.Ascending("Name").Ascending("CityCode"),
            new CreateIndexOptions
            {
                Name = NameCityCaseInsensitiveIndex,
                Collation = new Collation("en", strength: CollationStrength.Secondary),
            }),
    ];
}
