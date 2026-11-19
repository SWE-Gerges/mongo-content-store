using System.Globalization;
using Content.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Sync.Indexing;

public enum IndexVersionState
{
    /// <summary>Being filled by a rebuild; a later rebuild resumes into it.</summary>
    Building,

    /// <summary>The alias points at it.</summary>
    Live,

    /// <summary>Was live once and was replaced; kept so a rollback can point the alias back at it.</summary>
    Retired,

    /// <summary>Built, but verification refused it; kept for inspection until the next successful swap.</summary>
    Failed,
}

public sealed record VerificationCheck(string Name, bool Passed, string Detail);

/// <summary>The outcome of judging a freshly built index against its source.</summary>
public sealed record IndexVerification(
    bool Passed,
    DateTimeOffset VerifiedAt,
    long SourceCount,
    long IndexCount,
    long DeadLetters,
    IReadOnlyList<VerificationCheck> Checks)
{
    public string Summary => string.Join("; ", Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"));
}

/// <summary>One physical index behind an alias, as the version store remembers it.</summary>
public sealed record IndexVersionRecord(
    string IndexName,
    string Alias,
    int Version,
    IndexVersionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SwappedInAt,
    DateTimeOffset? RetiredAt,
    IndexVerification? Verification);

/// <summary>Naming of physical indexes: <c>{alias}-v{N}</c>.</summary>
public static class IndexVersionNaming
{
    public static string For(string alias, int version) => $"{alias}-v{version.ToString(CultureInfo.InvariantCulture)}";

    public static string Prefix(string alias) => alias + "-v";

    /// <returns>The version number when <paramref name="indexName"/> is a physical index of the alias.</returns>
    public static int? TryParse(string alias, string indexName)
    {
        var prefix = Prefix(alias);
        if (!indexName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = indexName.AsSpan(prefix.Length);
        return digits.Length is > 0 and < 9
            && digits.IndexOfAnyExceptInRange('0', '9') < 0
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? version
                : null;
    }
}

/// <summary>Remembers which physical indexes belong to an alias and what became of each, so a rollback only ever targets one that was live.</summary>
public interface IIndexVersionStore
{
    Task<IReadOnlyList<IndexVersionRecord>> ListAsync(string alias, CancellationToken cancellationToken);

    Task SaveAsync(IndexVersionRecord record, CancellationToken cancellationToken);

    Task DeleteAsync(string indexName, CancellationToken cancellationToken);
}

/// <summary>Version records in the <c>index_versions</c> collection, keyed by physical index name.</summary>
internal sealed class MongoIndexVersionStore(IMongoDatabaseFactory databases) : IIndexVersionStore
{
    public const string CollectionName = "index_versions";

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    public async Task<IReadOnlyList<IndexVersionRecord>> ListAsync(string alias, CancellationToken cancellationToken)
    {
        var stored = await Source()
            .Find(Filter.Eq("alias", alias))
            .Sort(Builders<BsonDocument>.Sort.Ascending("version"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return stored.Select(FromDocument).ToList();
    }

    public async Task SaveAsync(IndexVersionRecord record, CancellationToken cancellationToken) =>
        await Source()
            .ReplaceOneAsync(Filter.Eq("_id", record.IndexName), ToDocument(record), new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);

    public async Task DeleteAsync(string indexName, CancellationToken cancellationToken) =>
        await Source().DeleteOneAsync(Filter.Eq("_id", indexName), cancellationToken).ConfigureAwait(false);

    internal static BsonDocument ToDocument(IndexVersionRecord record) => new()
    {
        { "_id", record.IndexName },
        { "alias", record.Alias },
        { "version", record.Version },
        { "state", record.State.ToString() },
        { "createdAt", record.CreatedAt.UtcDateTime },
        { "swappedInAt", record.SwappedInAt is { } swapped ? swapped.UtcDateTime : BsonNull.Value },
        { "retiredAt", record.RetiredAt is { } retired ? retired.UtcDateTime : BsonNull.Value },
        { "verification", record.Verification is null ? BsonNull.Value : VerificationToDocument(record.Verification) },
    };

    internal static IndexVersionRecord FromDocument(BsonDocument document) => new(
        document["_id"].AsString,
        document["alias"].AsString,
        document["version"].ToInt32(),
        Enum.Parse<IndexVersionState>(document["state"].AsString),
        ToTime(document["createdAt"]),
        document["swappedInAt"].IsBsonNull ? null : ToTime(document["swappedInAt"]),
        document["retiredAt"].IsBsonNull ? null : ToTime(document["retiredAt"]),
        document["verification"].IsBsonNull ? null : VerificationFromDocument(document["verification"].AsBsonDocument));

    internal static BsonDocument VerificationToDocument(IndexVerification verification) => new()
    {
        { "passed", verification.Passed },
        { "verifiedAt", verification.VerifiedAt.UtcDateTime },
        { "sourceCount", verification.SourceCount },
        { "indexCount", verification.IndexCount },
        { "deadLetters", verification.DeadLetters },
        {
            "checks", new BsonArray(verification.Checks.Select(c => new BsonDocument { { "name", c.Name }, { "passed", c.Passed }, { "detail", c.Detail } }))
        },
    };

    internal static IndexVerification VerificationFromDocument(BsonDocument document) => new(
        document["passed"].ToBoolean(),
        ToTime(document["verifiedAt"]),
        document["sourceCount"].ToInt64(),
        document["indexCount"].ToInt64(),
        document["deadLetters"].ToInt64(),
        document["checks"].AsBsonArray
            .Select(c => new VerificationCheck(c["name"].AsString, c["passed"].ToBoolean(), c["detail"].AsString))
            .ToList());

    private static DateTimeOffset ToTime(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);

    private IMongoCollection<BsonDocument> Source() => databases.Get().GetCollection<BsonDocument>(CollectionName);
}
