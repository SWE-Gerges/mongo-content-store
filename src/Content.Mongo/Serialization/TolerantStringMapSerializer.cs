using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads a string-to-string map (language code to name) from a sub-document. Entries whose value is not a
/// string or number are reported and skipped; a value that is not a sub-document at all yields an empty map.
/// </summary>
public sealed class TolerantStringMapSerializer(IMalformedFieldSink? sink = null, string field = "")
    : SerializerBase<Dictionary<string, string>>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override Dictionary<string, string> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (reader.CurrentBsonType)
        {
            case BsonType.Document:
                reader.ReadStartDocument();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    var key = reader.ReadName();
                    if (Tolerant.TryReadScalarAsString(reader, out var value))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            result[key] = value;
                        }
                    }
                    else if (reader.CurrentBsonType == BsonType.Null)
                    {
                        reader.ReadNull();
                    }
                    else
                    {
                        Tolerant.Skip(reader, _sink, nameof(TolerantStringMapSerializer), field, "unsupported-entry");
                    }
                }

                reader.ReadEndDocument();
                break;

            case BsonType.Null:
                reader.ReadNull();
                break;

            default:
                Tolerant.Skip(reader, _sink, nameof(TolerantStringMapSerializer), field, "unsupported-type");
                break;
        }

        return result;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, Dictionary<string, string> value)
    {
        var writer = context.Writer;
        writer.WriteStartDocument();
        foreach (var (key, text) in value ?? [])
        {
            writer.WriteString(key, text);
        }

        writer.WriteEndDocument();
    }
}
