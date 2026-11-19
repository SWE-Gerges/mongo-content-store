using System.Globalization;
using Content.Store.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using OpenSearch.Client;

namespace Content.Sync.Search;

/// <summary>Maps stored hotels to search documents and source ids to search ids.</summary>
public static class HotelSearchDocumentMapper
{
    public static SearchDocument Map(HotelDocument hotel)
    {
        ArgumentNullException.ThrowIfNull(hotel);

        return new SearchDocument
        {
            Id = hotel.Id.ToString(CultureInfo.InvariantCulture),
            Code = hotel.Code,
            CanonicalId = hotel.CanonicalId,
            Name = hotel.Name,
            Names = hotel.Names,
            CityCode = hotel.CityCode,
            CountryCode = hotel.CountryCode,
            Rating = hotel.Rating,
            Address = hotel.Address,
            Location = hotel is { Latitude: { } lat, Longitude: { } lon } ? new GeoLocation(lat, lon) : null,
            Amenities = hotel.Amenities,
            Images = hotel.Images,
            ProviderKeys = hotel.Providers
                .Where(p => !string.IsNullOrWhiteSpace(p.Supplier) && !string.IsNullOrWhiteSpace(p.HotelId))
                .Select(p => $"{p.Supplier}:{p.HotelId}")
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Rooms = hotel.Rooms.Select(r => new SearchRoom { Id = r.Id, GroupId = r.GroupId, Name = r.Name }).ToList(),
            Providers = hotel.Providers.Select(p => new SearchProvider { Supplier = p.Supplier, HotelId = p.HotelId }).ToList(),
        };
    }

    /// <summary>
    /// Rebuilds the hotel a search document was made from, for reads answered by the index. Room attributes that are not
    /// kept in the index (the free-form extra data) are absent, so room detail is always read from the source.
    /// </summary>
    /// <returns>False when the id is not a number, which no document built by <see cref="Map"/> can have.</returns>
    public static bool TryToHotel(SearchDocument document, out HotelDocument hotel)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!long.TryParse(document.Id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            hotel = null!;
            return false;
        }

        hotel = new HotelDocument
        {
            Id = id,
            Code = document.Code,
            CanonicalId = document.CanonicalId,
            Name = document.Name,
            Names = document.Names ?? [],
            CityCode = document.CityCode,
            CountryCode = document.CountryCode,
            Rating = document.Rating,
            Address = document.Address,
            Latitude = document.Location?.Latitude,
            Longitude = document.Location?.Longitude,
            Images = document.Images ?? [],
            Amenities = document.Amenities ?? [],
            Rooms = (document.Rooms ?? []).Select(r => new RoomData { Id = r.Id, GroupIdRaw = r.GroupId, Name = r.Name }).ToList(),
            Providers = (document.Providers ?? []).Select(p => new ProviderRef { Supplier = p.Supplier, HotelId = p.HotelId }).ToList(),
        };
        return true;
    }

    /// <summary>Decodes a raw source document and maps it; false when it cannot be decoded (the sync dead-letters such documents).</summary>
    public static bool TryMap(BsonDocument source, out SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            document = Map(BsonSerializer.Deserialize<HotelDocument>(source));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or BsonSerializationException or InvalidCastException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>
    /// Renders a source <c>_id</c> exactly as stored: integers as base-10 digits (never through a floating-point
    /// surrogate, so 2^53 and 2^53+1 stay distinct), strings verbatim, object ids as hex. Used to name dead letters
    /// for documents that could not be decoded.
    /// </summary>
    public static string RenderId(BsonValue id) => id.BsonType switch
    {
        BsonType.Int32 => id.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => id.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.String => id.AsString,
        BsonType.ObjectId => id.AsObjectId.ToString(),
        _ => id.ToJson(),
    };
}
