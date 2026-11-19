using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads a list of strings from an array of strings (numbers are accepted and stringified), a single string,
/// or null. A malformed sub-document, or an element that is neither string nor number, is reported and skipped:
/// the result is an empty or partial list, never an exception, and never a lost document.
/// </summary>
public sealed class TolerantStringListSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<List<string>>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override List<string> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var result = new List<string>();

        switch (reader.CurrentBsonType)
        {
            case BsonType.Array:
                reader.ReadStartArray();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    if (Tolerant.TryReadScalarAsString(reader, out var item))
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            result.Add(item);
                        }
                    }
                    else if (reader.CurrentBsonType == BsonType.Null)
                    {
                        reader.ReadNull();
                    }
                    else
                    {
                        Tolerant.Skip(reader, _sink, nameof(TolerantStringListSerializer), field, "unsupported-element");
                    }
                }

                reader.ReadEndArray();
                break;

            case BsonType.String:
                var single = reader.ReadString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    result.Add(single);
                }

                break;

            case BsonType.Null:
                reader.ReadNull();
                break;

            default:
                Tolerant.Skip(reader, _sink, nameof(TolerantStringListSerializer), field, "unsupported-type");
                break;
        }

        return result;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<string> value)
    {
        var writer = context.Writer;
        writer.WriteStartArray();
        foreach (var item in value ?? [])
        {
            writer.WriteString(item);
        }

        writer.WriteEndArray();
    }
}
