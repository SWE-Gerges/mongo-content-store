using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads a loosely typed scalar (an identifier that is an int in one document and a string in the next) into
/// a boxed <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="double"/> or <see cref="bool"/>,
/// preserving the stored type for a faithful round trip. The stock object serializer throws on a sub-document
/// without a type discriminator; this one reports it and yields <c>null</c> instead.
/// </summary>
public sealed class ScalarObjectSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<object?>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override object? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        switch (reader.CurrentBsonType)
        {
            case BsonType.String:
                return reader.ReadString();
            case BsonType.Int32:
                return reader.ReadInt32();
            case BsonType.Int64:
                return reader.ReadInt64();
            case BsonType.Double:
                return reader.ReadDouble();
            case BsonType.Boolean:
                return reader.ReadBoolean();
            case BsonType.Null:
                reader.ReadNull();
                return null;
            default:
                Tolerant.Skip(reader, _sink, nameof(ScalarObjectSerializer), field, "unsupported-type");
                return null;
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object? value)
    {
        var writer = context.Writer;
        switch (value)
        {
            case null:
                writer.WriteNull();
                break;
            case string text:
                writer.WriteString(text);
                break;
            case int number:
                writer.WriteInt32(number);
                break;
            case long number:
                writer.WriteInt64(number);
                break;
            case double number:
                writer.WriteDouble(number);
                break;
            case bool flag:
                writer.WriteBoolean(flag);
                break;
            default:
                throw new BsonSerializationException($"{nameof(ScalarObjectSerializer)} cannot write a value of type {value.GetType().Name}.");
        }
    }
}
