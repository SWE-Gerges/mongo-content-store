using MongoDB.Bson.Serialization.Attributes;

namespace Content.Store.Models;

/// <summary>
/// One hotel as stored in a per-supplier collection or the merged <c>hotels</c> master. Supplier data is messy,
/// so every member except <see cref="Id"/> is decoded with a tolerant serializer (see <c>HotelBsonMappings</c>):
/// a value that cannot be read drops that field and never the document.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class HotelDocument
{
    /// <summary>The supplier's numeric hotel id.</summary>
    [BsonId]
    public long Id { get; set; }

    /// <summary>Alphanumeric supplier code, for suppliers that key hotels by code.</summary>
    public string? Code { get; set; }

    public string? CanonicalId { get; set; }

    public string? Name { get; set; }

    /// <summary>Language code to localised name.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    public string? CityCode { get; set; }

    public string? CountryCode { get; set; }

    /// <summary>Star rating; <c>0</c> when the supplier sent nothing usable.</summary>
    public double Rating { get; set; }

    public string? Address { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public List<string> Images { get; set; } = [];

    public List<string> Amenities { get; set; } = [];

    public List<RoomData> Rooms { get; set; } = [];

    /// <summary>Supplier hotel ids this (merged) hotel was built from.</summary>
    public List<ProviderRef> Providers { get; set; } = [];
}
