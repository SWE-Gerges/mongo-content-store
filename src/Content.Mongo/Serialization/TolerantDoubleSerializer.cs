using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Content.Mongo.Serialization;

/// <summary>
/// Reads a double from a BSON double, int32, int64, decimal128 or numeric string (for ratings that suppliers
/// store as <c>"4.5"</c>, <c>4</c> or <c>4.5</c>). Whole-number types are widened without loss. A value that cannot be
/// read is reported and the member keeps its default (<c>0</c>); use <see cref="TolerantNullableDoubleSerializer"/>
/// where "absent" must stay distinguishable from zero.
/// </summary>
public sealed class TolerantDoubleSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<double>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override double Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        Tolerant.TryReadDouble(context.Reader, _sink, nameof(TolerantDoubleSerializer), field, out var value) ? value : 0d;

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, double value) =>
        context.Writer.WriteDouble(value);
}

/// <summary>
/// Same tolerance as <see cref="TolerantDoubleSerializer"/> but an unreadable or null value yields <c>null</c>.
/// </summary>
public sealed class TolerantNullableDoubleSerializer(IMalformedFieldSink? sink = null, string field = "") : SerializerBase<double?>
{
    private readonly IMalformedFieldSink _sink = sink ?? NullMalformedFieldSink.Instance;

    public override double? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        Tolerant.TryReadDouble(context.Reader, _sink, nameof(TolerantNullableDoubleSerializer), field, out var value) ? value : null;

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, double? value)
    {
        if (value is null)
        {
            context.Writer.WriteNull();
            return;
        }

        context.Writer.WriteDouble(value.Value);
    }
}
