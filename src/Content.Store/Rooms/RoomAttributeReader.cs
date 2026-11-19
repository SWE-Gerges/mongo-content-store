using System.Globalization;
using Content.Store.Models;
using MongoDB.Bson;

namespace Content.Store.Rooms;

/// <summary>
/// Reads supplier room attributes tolerantly: the free-form <c>Extra</c> document wins, the typed
/// <c>ExtraFields</c> bag fills any gap. Wrong-typed or missing values are treated as absent, never as errors.
/// </summary>
internal readonly struct RoomAttributeReader(RoomData room)
{
    public string? GetString(string name, Func<RoomExtraFields, string?> typedFallback)
    {
        var fromExtra = FromExtra(name);
        if (fromExtra is not null)
        {
            return fromExtra;
        }

        var typed = room.ExtraFields is null ? null : typedFallback(room.ExtraFields);
        return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
    }

    public int? GetInt(string name, Func<RoomExtraFields, string?> typedFallback)
    {
        var text = GetString(name, typedFallback);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
    }

    public IReadOnlyList<string> GetStringList(string name, Func<RoomExtraFields, IReadOnlyList<string>?> typedFallback)
    {
        if (TryFindValue(name, out var value))
        {
            var fromExtra = ToStrings(value);
            if (fromExtra.Count > 0)
            {
                return fromExtra;
            }
        }

        return room.ExtraFields is null ? [] : typedFallback(room.ExtraFields) ?? [];
    }

    private string? FromExtra(string name) =>
        TryFindValue(name, out var value) ? ToScalarString(value) : null;

    private bool TryFindValue(string name, out BsonValue value)
    {
        value = BsonNull.Value;
        if (room.Extra is null)
        {
            return false;
        }

        foreach (var element in room.Extra.Elements)
        {
            if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = element.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ToScalarString(BsonValue value) => value.BsonType switch
    {
        BsonType.String => string.IsNullOrWhiteSpace(value.AsString) ? null : value.AsString.Trim(),
        BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static List<string> ToStrings(BsonValue value)
    {
        var result = new List<string>();
        if (value.IsBsonArray)
        {
            foreach (var item in value.AsBsonArray)
            {
                if (ToScalarString(item) is { } text)
                {
                    result.Add(text);
                }
            }
        }
        else if (ToScalarString(value) is { } single)
        {
            result.Add(single);
        }

        return result;
    }
}
