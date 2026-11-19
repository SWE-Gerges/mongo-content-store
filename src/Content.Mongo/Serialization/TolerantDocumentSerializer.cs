using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads one embedded document into <typeparamref name="T"/>. The sub-document is buffered first, so when it
/// cannot be mapped (wrong shape, bad nested value) the outer reader is still in a consistent state: the field
/// is reported and dropped to <c>null</c> and the parent document keeps decoding.
/// </summary>
public sealed class TolerantDocumentSerializer<T>(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<T?>
    where T : class
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override T? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        switch (reader.CurrentBsonType)
        {
            case BsonType.Document:
                var buffered = BsonDocumentSerializer.Instance.Deserialize(context);
                return TolerantDocument.TryMap<T>(buffered, _sink, nameof(TolerantDocumentSerializer<T>), field);
            case BsonType.Null:
                reader.ReadNull();
                return null;
            default:
                Tolerant.Skip(reader, _sink, nameof(TolerantDocumentSerializer<T>), field, "unsupported-type");
                return null;
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T? value)
    {
        if (value is null)
        {
            context.Writer.WriteNull();
            return;
        }

        BsonSerializer.LookupSerializer<T>().Serialize(context, value);
    }
}

/// <summary>
/// Reads an array of embedded documents into a list. Each element is buffered and mapped on its own, so one
/// malformed element is reported and skipped instead of failing the list (or the parent document).
/// </summary>
public sealed class TolerantDocumentListSerializer<T>(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<List<T>>
    where T : class
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override List<T> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var result = new List<T>();

        switch (reader.CurrentBsonType)
        {
            case BsonType.Array:
                reader.ReadStartArray();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    if (reader.CurrentBsonType == BsonType.Document)
                    {
                        var buffered = BsonDocumentSerializer.Instance.Deserialize(context);
                        var item = TolerantDocument.TryMap<T>(buffered, _sink, nameof(TolerantDocumentListSerializer<T>), field);
                        if (item is not null)
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
                        Tolerant.Skip(reader, _sink, nameof(TolerantDocumentListSerializer<T>), field, "unsupported-element");
                    }
                }

                reader.ReadEndArray();
                break;

            case BsonType.Null:
                reader.ReadNull();
                break;

            default:
                Tolerant.Skip(reader, _sink, nameof(TolerantDocumentListSerializer<T>), field, "unsupported-type");
                break;
        }

        return result;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<T> value)
    {
        var writer = context.Writer;
        var itemSerializer = BsonSerializer.LookupSerializer<T>();
        writer.WriteStartArray();
        foreach (var item in value ?? [])
        {
            itemSerializer.Serialize(context, item);
        }

        writer.WriteEndArray();
    }
}

internal static class TolerantDocument
{
    public static T? TryMap<T>(BsonDocument buffered, IMalformedFieldSink sink, string serializer, string field)
        where T : class
    {
        try
        {
            return BsonSerializer.Deserialize<T>(buffered);
        }
        catch (Exception ex) when (ex is FormatException or BsonSerializationException or InvalidCastException or ArgumentException or OverflowException)
        {
            sink.Report(new MalformedField(serializer, field, BsonType.Document, "unmappable-document"));
            return null;
        }
    }
}
