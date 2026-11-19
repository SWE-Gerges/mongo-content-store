using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads image URLs from an array whose elements are either plain strings or objects carrying a
/// <c>url</c>/<c>Url</c> field (any casing). A lone string or object instead of an array is accepted as a
/// one-element list. Elements without a usable URL are reported and skipped.
/// </summary>
public sealed class ImageUrlListSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<List<string>>
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
                    ReadElement(context, result);
                }

                reader.ReadEndArray();
                break;

            case BsonType.String:
            case BsonType.Document:
                ReadElement(context, result);
                break;

            case BsonType.Null:
                reader.ReadNull();
                break;

            default:
                Tolerant.Skip(reader, _sink, nameof(ImageUrlListSerializer), field, "unsupported-type");
                break;
        }

        return result;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<string> value)
    {
        var writer = context.Writer;
        writer.WriteStartArray();
        foreach (var url in value ?? [])
        {
            writer.WriteString(url);
        }

        writer.WriteEndArray();
    }

    private void ReadElement(BsonDeserializationContext context, List<string> result)
    {
        var reader = context.Reader;
        switch (reader.CurrentBsonType)
        {
            case BsonType.String:
                AddIfUsable(reader.ReadString(), result);
                break;

            case BsonType.Document:
                reader.ReadStartDocument();
                string? url = null;
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    var name = reader.ReadName();
                    if (url is null && string.Equals(name, "url", StringComparison.OrdinalIgnoreCase) && reader.CurrentBsonType == BsonType.String)
                    {
                        url = reader.ReadString();
                    }
                    else
                    {
                        reader.SkipValue();
                    }
                }

                reader.ReadEndDocument();
                if (!AddIfUsable(url, result))
                {
                    _sink.Report(new MalformedField(nameof(ImageUrlListSerializer), field, BsonType.Document, "missing-url"));
                }

                break;

            case BsonType.Null:
                reader.ReadNull();
                break;

            default:
                Tolerant.Skip(reader, _sink, nameof(ImageUrlListSerializer), field, "unsupported-element");
                break;
        }
    }

    private static bool AddIfUsable(string? url, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        result.Add(url);
        return true;
    }
}
