using System.Diagnostics.Metrics;
using System.Globalization;
using Content.Store;
using Content.Store.Configuration;
using Content.Store.Models;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Content.Reads;

/// <summary>Where the documents of a read came from.</summary>
public enum ContentSource
{
    /// <summary>Everything was served by the search index.</summary>
    Index,

    /// <summary>Everything was served by MongoDB (the index had none of it, or did not answer).</summary>
    Mongo,

    /// <summary>Some documents came from the index and the rest were completed from MongoDB.</summary>
    Mixed,
}

public static class ContentSourceExtensions
{
    /// <summary>The value of the <c>X-Content-Source</c> response header.</summary>
    public static string ToHeaderValue(this ContentSource source) => source switch
    {
        ContentSource.Index => "index",
        ContentSource.Mongo => "mongo",
        _ => "mixed",
    };
}

public sealed record ReadResult<T>(IReadOnlyList<T> Items, ContentSource Source);

/// <summary>Hotels read from the search index first, with MongoDB completing whatever the index did not have.</summary>
public interface IContentReader
{
    /// <exception cref="UnknownSupplierException">The supplier has no route.</exception>
    Task<ReadResult<HotelDocument>> GetByIdsAsync(string supplier, IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <exception cref="UnknownSupplierException">The supplier has no route.</exception>
    Task<ReadResult<HotelDocument>> GetByCodesAsync(string supplier, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
}

/// <summary>Told which keys the search index lacks although the source has them: the index is behind and wants a resync.</summary>
public interface IIndexMissSink
{
    void Record(string supplier, IReadOnlyCollection<string> keys);
}

/// <summary>Counts index misses through the <c>Content.Reads</c> meter as <c>content.reads.index_misses</c>.</summary>
public sealed class MetricsIndexMissSink : IIndexMissSink, IDisposable
{
    public const string MeterName = "Content.Reads";
    public const string CounterName = "content.reads.index_misses";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _counter;

    public MetricsIndexMissSink() =>
        _counter = _meter.CreateCounter<long>(CounterName, unit: "{document}", description: "Documents the search index lacked although MongoDB had them.");

    public void Record(string supplier, IReadOnlyCollection<string> keys) =>
        _counter.Add(keys.Count, new KeyValuePair<string, object?>("supplier", supplier));

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// The read path. Ids (or codes) are looked up in the supplier's search index alias first; whatever the index does
/// not have is completed from MongoDB (partial-hit completion). Two guards keep the fallback honest:
/// <list type="bullet">
/// <item>A key counts as missing from the index, and is reported to <see cref="IIndexMissSink"/>, only when the index
/// actually answered. If the index is unreachable, refuses the request or exceeds <see cref="ReadsOptions.IndexTimeout"/>
/// the whole read is served from MongoDB and nothing is recorded, because an outage says nothing about the documents.</item>
/// <item>A caller that disconnects cancels the read; that is never mistaken for a slow index.</item>
/// </list>
/// MongoDB failures (including a server-side <c>MaxTime</c> expiry) are not swallowed: a half-answered read is never returned silently.
/// </summary>
internal sealed class ContentReader(
    ReadRouteTable routes,
    ISearchDocumentReader index,
    IHotelContentStore store,
    IIndexMissSink misses,
    IOptions<ReadsOptions> options,
    IOptions<ContentStoreOptions> storeOptions,
    TimeProvider timeProvider,
    ILogger<ContentReader> logger) : IContentReader
{
    public async Task<ReadResult<HotelDocument>> GetByIdsAsync(string supplier, IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var route = routes.Get(supplier);
        EnforceLimit(ids.Count);

        var keys = ids.Distinct().ToList();
        return await ReadAsync(
                supplier,
                keys,
                async token =>
                {
                    var found = await index.GetByIdsAsync(route.IndexAlias, [.. keys.Select(k => k.ToString(CultureInfo.InvariantCulture))], token).ConfigureAwait(false);
                    return found.Values;
                },
                hotel => hotel.Id,
                key => key.ToString(CultureInfo.InvariantCulture),
                (missing, token) => store.GetByIdsAsync(supplier, missing, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReadResult<HotelDocument>> GetByCodesAsync(string supplier, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var route = routes.Get(supplier);
        EnforceLimit(codes.Count);

        var keys = codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToList();
        return await ReadAsync(
                supplier,
                keys,
                async token => await index.GetByCodesAsync(route.IndexAlias, keys, token).ConfigureAwait(false),
                hotel => hotel.Code ?? string.Empty,
                key => key,
                (missing, token) => store.GetByCodesAsync(supplier, missing, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReadResult<HotelDocument>> ReadAsync<TKey>(
        string supplier,
        IReadOnlyList<TKey> keys,
        Func<CancellationToken, Task<IEnumerable<SearchDocument>>> readIndex,
        Func<HotelDocument, TKey> keyOf,
        Func<TKey, string> render,
        Func<IReadOnlyCollection<TKey>, CancellationToken, Task<IReadOnlyList<HotelDocument>>> readMongo,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        if (keys.Count == 0)
        {
            return new ReadResult<HotelDocument>([], ContentSource.Index);
        }

        var (fromIndex, answered) = await TryReadIndexAsync(supplier, readIndex, cancellationToken).ConfigureAwait(false);
        var found = fromIndex.Select(keyOf).ToHashSet();
        var missing = keys.Where(key => !found.Contains(key)).ToList();
        if (missing.Count == 0)
        {
            return new ReadResult<HotelDocument>(fromIndex, ContentSource.Index);
        }

        var fromMongo = await readMongo(missing, cancellationToken).ConfigureAwait(false);

        if (answered && fromMongo.Count > 0)
        {
            // MongoDB has documents the answering index lacked: the index is behind. Keys that exist nowhere are not a signal.
            misses.Record(supplier, [.. fromMongo.Select(keyOf).Select(render)]);
        }

        var source = fromIndex.Count == 0 ? ContentSource.Mongo : fromMongo.Count == 0 ? ContentSource.Index : ContentSource.Mixed;
        return new ReadResult<HotelDocument>([.. fromIndex, .. fromMongo], source);
    }

    private async Task<(IReadOnlyList<HotelDocument> Documents, bool Answered)> TryReadIndexAsync(
        string supplier,
        Func<CancellationToken, Task<IEnumerable<SearchDocument>>> readIndex,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.Value.IndexTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var documents = await readIndex(linked.Token).ConfigureAwait(false);
            var hotels = new List<HotelDocument>();
            foreach (var document in documents)
            {
                if (HotelSearchDocumentMapper.TryToHotel(document, out var hotel))
                {
                    hotels.Add(hotel);
                }
            }

            return (hotels, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            logger.LogWarning("The search index did not answer within {Timeout} for supplier {Supplier}; serving from MongoDB.", options.Value.IndexTimeout, supplier);
            return ([], false);
        }
        catch (Exception ex) when (ex is SearchIndexUnavailableException or SearchIndexException)
        {
            logger.LogWarning(ex, "The search index could not serve supplier {Supplier}; serving from MongoDB.", supplier);
            return ([], false);
        }
    }

    private void EnforceLimit(int count)
    {
        if (count > storeOptions.Value.MaxIdsPerRequest)
        {
            throw new InvalidQueryException($"At most {storeOptions.Value.MaxIdsPerRequest} keys may be requested at once.");
        }
    }
}
