using MongoDB.Bson.Serialization.Attributes;

namespace Content.Store.Models;

/// <summary>Points from a merged hotel back to one supplier's own id for it.</summary>
[BsonIgnoreExtraElements]
public sealed class ProviderRef
{
    public string? Supplier { get; set; }

    /// <summary>Stored as a string or a number depending on the supplier; always read as a string.</summary>
    public string? HotelId { get; set; }
}
