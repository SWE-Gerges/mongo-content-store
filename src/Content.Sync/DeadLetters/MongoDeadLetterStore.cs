using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Content.Mongo;
using Content.Store;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Sync.DeadLetters;

/// <summary>Stores dead letters in <c>_sync_deadletter</c> as <c>{collection, docId, reason, firstSeenAt, attempts, payloadHash}</c>.</summary>
internal sealed class MongoDeadLetterStore(IMongoDatabaseFactory databases, TimeProvider clock) : IDeadLetterStore
{
    public const string CollectionName = "_sync_deadletter";
    private const int MaxPageSize = 200;
    private const string CursorPrefix = "d1";

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    public async Task WriteAsync(string collection, IReadOnlyCollection<DeadLetterEntry> entries, string? scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var update = Builders<BsonDocument>.Update;
        var models = entries.Select(entry => new UpdateOneModel<BsonDocument>(
            Filter.Eq("_id", Key(collection, entry.DocId)),
            update.Combine(
                update.SetOnInsert("collection", collection),
                update.SetOnInsert("docId", entry.DocId),
                update.SetOnInsert("firstSeenAt", now),
                update.Set("reason", entry.Reason),
                update.Set("payloadHash", entry.PayloadHash),
                update.Set("scope", scope is null ? (BsonValue)BsonNull.Value : scope),
                update.Inc("attempts", 1)))
        {
            IsUpsert = true,
        }).ToList();

        await Source()
            .BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeadLetterPage> ListAsync(string? collection, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        var filter = string.IsNullOrWhiteSpace(collection) ? Filter.Empty : Filter.Eq("collection", collection);
        if (!string.IsNullOrEmpty(cursor))
        {
            var (milliseconds, id) = DecodeCursor(cursor);
            var seen = DateTime.UnixEpoch.AddMilliseconds(milliseconds);

            // Newest first, ties by id: strictly older, or the same millisecond with a later id.
            filter = Filter.And(
                filter,
                Filter.Or(
                    Filter.Lt("firstSeenAt", seen),
                    Filter.And(Filter.Eq("firstSeenAt", seen), Filter.Gt("_id", id))));
        }

        var fetched = await Source()
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("firstSeenAt").Ascending("_id"))
            .Limit(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = fetched.Count > pageSize;
        if (hasMore)
        {
            fetched.RemoveAt(fetched.Count - 1);
        }

        var items = fetched
            .Select(d => new DeadLetterRecord(
                d["collection"].AsString,
                d["docId"].AsString,
                d["reason"].AsString,
                new DateTimeOffset(d["firstSeenAt"].ToUniversalTime(), TimeSpan.Zero),
                d["attempts"].ToInt32(),
                d["payloadHash"].AsString))
            .ToList();
        var next = hasMore ? EncodeCursor(fetched[^1]["firstSeenAt"].AsBsonDateTime.MillisecondsSinceEpoch, fetched[^1]["_id"].AsString) : null;
        return new DeadLetterPage(items, next);
    }

    public async Task<long> CountAsync(string collection, string? scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var filter = Filter.Eq("collection", collection);
        if (scope is not null)
        {
            filter = Filter.And(filter, Filter.Eq("scope", scope));
        }

        return await Source().CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string collection, IReadOnlyCollection<string> docIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (docIds.Count == 0)
        {
            return;
        }

        await Source()
            .DeleteManyAsync(Filter.In("_id", docIds.Select(id => Key(collection, id))), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The cursor carries the timestamp at millisecond resolution (what MongoDB stores), so comparisons against it
    /// neither skip nor repeat rows that share a millisecond.
    /// </summary>
    internal static string EncodeCursor(long milliseconds, string id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CursorPrefix}\n{milliseconds.ToString(CultureInfo.InvariantCulture)}\n{id}"));

    internal static (long Milliseconds, string Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('\n', 3);
            if (parts is [CursorPrefix, var ms, var id] && long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return (milliseconds, id);
            }
        }
        catch (FormatException)
        {
            // Falls through to the single failure below.
        }

        throw new InvalidCursorException();
    }

    private static string Key(string collection, string docId) => $"{collection}|{docId}";

    private IMongoCollection<BsonDocument> Source() => databases.Get().GetCollection<BsonDocument>(CollectionName);
}
