using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads a BSON string or any numeric type as a string (suppliers store the same identifier as
/// <c>"123"</c> in one document and <c>123</c> in the next). Anything else (sub-document, array, boolean ...)
/// drops the field to <c>null</c> and is reported; the document survives.
/// </summary>
public sealed class StringOrNumberSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<string?>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override string? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (Tolerant.TryReadScalarAsString(reader, out var value))
        {
            return value;
        }

        if (reader.CurrentBsonType == BsonType.Null)
        {
            reader.ReadNull();
            return null;
        }

        Tolerant.Skip(reader, _sink, nameof(StringOrNumberSerializer), field, "unsupported-type");
        return null;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, string? value)
    {
        if (value is null)
        {
            context.Writer.WriteNull();
            return;
        }

        context.Writer.WriteString(value);
    }
}
