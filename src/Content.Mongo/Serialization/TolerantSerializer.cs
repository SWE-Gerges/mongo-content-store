using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Content.Mongo.Serialization;

/// <summary>
/// Shared plumbing for the tolerant serializers: defect reporting and lenient scalar reads.
/// None of the read helpers throw on unexpected data; they report it, consume the value and return a miss.
/// </summary>
internal static class Tolerant
{
    public static void Skip(IBsonReader reader, IMalformedFieldSink sink, string serializer, string field, string reason)
    {
        sink.Report(new MalformedField(serializer, field, reader.CurrentBsonType, reason));
        reader.SkipValue();
    }

    /// <summary>Reads a string or numeric BSON value as an invariant string; returns false (value NOT consumed) otherwise.</summary>
    public static bool TryReadScalarAsString(IBsonReader reader, out string? value)
    {
        switch (reader.CurrentBsonType)
        {
            case BsonType.String:
                value = reader.ReadString();
                return true;
            case BsonType.Int32:
                value = reader.ReadInt32().ToString(CultureInfo.InvariantCulture);
                return true;
            case BsonType.Int64:
                value = reader.ReadInt64().ToString(CultureInfo.InvariantCulture);
                return true;
            case BsonType.Double:
                value = reader.ReadDouble().ToString("R", CultureInfo.InvariantCulture);
                return true;
            case BsonType.Decimal128:
                value = reader.ReadDecimal128().ToString();
                return true;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>
    /// Reads any numeric BSON type or a numeric string as a finite double. Whole-number types are widened
    /// (so a rating stored as the integer 4 reads as 4.0); a string is parsed with the invariant culture.
    /// Returns false (value consumed and reported) when the value is not usable.
    /// </summary>
    public static bool TryReadDouble(IBsonReader reader, IMalformedFieldSink sink, string serializer, string field, out double value)
    {
        value = 0;
        switch (reader.CurrentBsonType)
        {
            case BsonType.Int32:
                value = reader.ReadInt32();
                return true;
            case BsonType.Int64:
                value = reader.ReadInt64();
                return true;
            case BsonType.Double:
                value = reader.ReadDouble();
                return Finite(ref value, sink, serializer, field);
            case BsonType.Decimal128:
                try
                {
                    value = Decimal128.ToDouble(reader.ReadDecimal128());
                }
                catch (OverflowException)
                {
                    sink.Report(new MalformedField(serializer, field, BsonType.Decimal128, "out-of-range"));
                    return false;
                }

                return Finite(ref value, sink, serializer, field);
            case BsonType.String:
                var text = reader.ReadString();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
                {
                    return true;
                }

                sink.Report(new MalformedField(serializer, field, BsonType.String, "not-a-number"));
                value = 0;
                return false;
            case BsonType.Null:
                reader.ReadNull();
                return false;
            default:
                Skip(reader, sink, serializer, field, "unsupported-type");
                return false;
        }
    }

    private static bool Finite(ref double value, IMalformedFieldSink sink, string serializer, string field)
    {
        if (double.IsFinite(value))
        {
            return true;
        }

        sink.Report(new MalformedField(serializer, field, BsonType.Double, "not-finite"));
        value = 0;
        return false;
    }
}
