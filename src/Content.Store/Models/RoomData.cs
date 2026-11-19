using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Content.Store.Models;

// Embedded document: its "Id" is an ordinary element, not the document _id.
[BsonNoId]
[BsonIgnoreExtraElements]
public sealed class RoomData
{
    public string? Id { get; set; }

    /// <summary>The group id exactly as stored; suppliers send an int in one document and a string in the next.</summary>
    [BsonElement("GroupId")]
    public object? GroupIdRaw { get; set; }

    /// <summary>The group id as a string, whatever its stored type.</summary>
    [BsonIgnore]
    public string? GroupId => GroupIdRaw switch
    {
        string text => text,
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    public string? Name { get; set; }

    /// <summary>Free-form, supplier-specific attributes, kept exactly as received.</summary>
    public BsonDocument? Extra { get; set; }

    /// <summary>Typed bag of the attributes the mappers know about; consulted when <see cref="Extra"/> lacks a value.</summary>
    public RoomExtraFields? ExtraFields { get; set; }
}

/// <summary>Strongly named room attributes. Values are tolerant strings because suppliers mix numbers and text.</summary>
[BsonIgnoreExtraElements]
public sealed class RoomExtraFields
{
    public string? Pax { get; set; }

    public string? RoomType { get; set; }

    public List<string> CharacteristicCodes { get; set; } = [];

    public string? Class { get; set; }

    public string? Quality { get; set; }

    public string? Bedding { get; set; }

    public string? Bathroom { get; set; }
}
