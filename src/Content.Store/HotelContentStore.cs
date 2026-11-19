using System.Globalization;
using System.Text.RegularExpressions;
using Content.Mongo.Repository;
using Content.Store.Configuration;
using Content.Store.Models;
using Content.Store.Rooms;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Store;

internal sealed class HotelContentStore(
    IMongoRepository<HotelDocument> repository,
    ICollectionNaming naming,
    RoomExtraMapperRegistry roomMappers,
    IOptions<ContentStoreOptions> options) : IHotelContentStore
{
    private const int MaxNameLength = 100;

    /// <summary>Case-insensitive comparison done by the server so the collation index can answer it.</summary>
    internal static readonly Collation CaseInsensitive = new("en", strength: CollationStrength.Secondary);

    private static FilterDefinitionBuilder<HotelDocument> Filter => Builders<HotelDocument>.Filter;

    private readonly ContentStoreOptions _options = options.Value;

    public async Task<IReadOnlyList<HotelDocument>> GetByIdsAsync(
        string supplier,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var collection = CollectionFor(supplier);
        EnforceIdLimit(ids.Count);

        // Filter.In over the numeric _id keeps the query on the _id index. Comparing ids.Contains(h.Id.ToString())
        // would force a per-document conversion that no index can serve.
        return await repository
            .FilterAsync(collection, Filter.In(h => h.Id, ids.Distinct()), new QueryOptions<HotelDocument> { MaxTime = _options.ReadMaxTime }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HotelDocument>> GetByCodesAsync(
        string supplier,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var collection = CollectionFor(supplier);
        EnforceIdLimit(codes.Count);

        var wanted = codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        return await repository
            .FilterAsync(collection, Filter.In(h => h.Code, wanted), new QueryOptions<HotelDocument> { MaxTime = _options.ReadMaxTime }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CityPage> GetByCityAsync(
        string supplier,
        string cityCode,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var collection = CollectionFor(supplier);
        if (string.IsNullOrWhiteSpace(cityCode))
        {
            throw new InvalidQueryException("cityCode is required.");
        }

        var size = pageSize ?? _options.DefaultPageSize;
        if (size < 1 || size > _options.MaxPageSize)
        {
            throw new InvalidQueryException($"pageSize must be between 1 and {_options.MaxPageSize}.");
        }

        BsonValue? afterId = string.IsNullOrEmpty(cursor) ? null : new BsonInt64(HotelCursor.Decode(cursor));

        // Only _id is projected, and the {CityCode, _id} index carries both, so the page is answered from the index.
        var page = await repository
            .PageByIdAsync(
                collection,
                afterId,
                size,
                hotel => new BsonInt64(hotel.Id),
                Filter.Eq(h => h.CityCode, cityCode),
                Builders<HotelDocument>.Projection.Include(h => h.Id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var ids = page.Items.Select(h => h.Id).ToList();
        var next = page.HasMore && ids.Count > 0 ? HotelCursor.Encode(ids[^1]) : null;
        return new CityPage(ids, next);
    }

    public async Task<IReadOnlyList<HotelSummary>> SearchByNameAsync(
        string name,
        string? countryCode,
        NameSearchMode mode,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidQueryException("name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new InvalidQueryException($"name must be at most {MaxNameLength} characters.");
        }

        var max = limit ?? _options.MaxSearchResults;
        if (max < 1 || max > _options.MaxSearchResults)
        {
            throw new InvalidQueryException($"limit must be between 1 and {_options.MaxSearchResults}.");
        }

        var nameFilter = mode switch
        {
            NameSearchMode.Anchored => Filter.Regex(h => h.Name, new BsonRegularExpression("^" + Regex.Escape(trimmed))),
            NameSearchMode.Contains => Filter.Regex(h => h.Name, new BsonRegularExpression(Regex.Escape(trimmed), "i")),
            NameSearchMode.CaseInsensitive => Filter.Eq(h => h.Name, trimmed),
            _ => throw new InvalidQueryException($"Unknown search mode '{mode}'."),
        };

        var filter = string.IsNullOrWhiteSpace(countryCode)
            ? nameFilter
            : Filter.And(nameFilter, Filter.Eq(h => h.CountryCode, countryCode.Trim()));

        var queryOptions = new QueryOptions<HotelDocument>
        {
            Projection = Builders<HotelDocument>.Projection
                .Include(h => h.Id)
                .Include(h => h.Code)
                .Include(h => h.Name)
                .Include(h => h.CityCode)
                .Include(h => h.CountryCode)
                .Include(h => h.Rating),
            Limit = max,
            Collation = mode == NameSearchMode.CaseInsensitive ? CaseInsensitive : null,
            MaxTime = _options.SearchMaxTime,
        };

        var hotels = await repository
            .FilterAsync(naming.Master, filter, queryOptions, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return hotels
            .Select(h => new HotelSummary(h.Id, h.Code, h.Name, h.CityCode, h.CountryCode, h.Rating))
            .ToList();
    }

    public async Task<IReadOnlyList<HotelDocument>> FindByProviderAsync(
        string supplier,
        string providerHotelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supplier) || string.IsNullOrWhiteSpace(providerHotelId))
        {
            throw new InvalidQueryException("supplier and hotel id are required.");
        }

        // The same supplier id is stored as "123" in some documents and 123 in others; $in matches both forms.
        var variants = new BsonArray { providerHotelId };
        if (long.TryParse(providerHotelId, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            && numeric.ToString(CultureInfo.InvariantCulture) == providerHotelId)
        {
            variants.Add(numeric);
        }

        var filter = new BsonDocument(
            nameof(HotelDocument.Providers),
            new BsonDocument("$elemMatch", new BsonDocument
            {
                { nameof(ProviderRef.Supplier), supplier },
                { nameof(ProviderRef.HotelId), new BsonDocument("$in", variants) },
            }));

        return await repository
            .FilterAsync(naming.Master, new BsonDocumentFilterDefinition<HotelDocument>(filter), new QueryOptions<HotelDocument> { Limit = 20 }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RoomDetailResult> GetRoomDetailAsync(
        string supplier,
        long hotelId,
        string roomId,
        CancellationToken cancellationToken = default)
    {
        var collection = CollectionFor(supplier);
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new InvalidQueryException("roomId is required.");
        }

        var hotel = await repository.GetByIdAsync(collection, hotelId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (hotel is null)
        {
            return new RoomDetailResult(RoomDetailStatus.HotelNotFound, null);
        }

        var room = hotel.Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.Ordinal));
        if (room is null)
        {
            return new RoomDetailResult(RoomDetailStatus.RoomNotFound, null);
        }

        var mapped = roomMappers.Map(supplier, room);
        return mapped.HasMapper
            ? new RoomDetailResult(RoomDetailStatus.Found, mapped.Detail)
            : new RoomDetailResult(RoomDetailStatus.NoMapper, null);
    }

    private string CollectionFor(string supplier)
    {
        if (string.IsNullOrWhiteSpace(supplier) || !_options.Suppliers.Contains(supplier, StringComparer.Ordinal))
        {
            throw new UnknownSupplierException(supplier ?? string.Empty);
        }

        return naming.ForSupplier(supplier);
    }

    private void EnforceIdLimit(int count)
    {
        if (count > _options.MaxIdsPerRequest)
        {
            throw new InvalidQueryException($"At most {_options.MaxIdsPerRequest} keys may be requested at once.");
        }
    }
}
