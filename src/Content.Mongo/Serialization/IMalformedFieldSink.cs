using MongoDB.Bson;

namespace Content.Mongo.Serialization;

/// <summary>A value that a tolerant serializer could not interpret and therefore dropped.</summary>
/// <param name="Serializer">Name of the serializer that tolerated the defect.</param>
/// <param name="Field">Logical field name the serializer was bound to (empty when unknown).</param>
/// <param name="BsonType">The BSON type that was found.</param>
/// <param name="Reason">Short machine-friendly reason, e.g. <c>unsupported-type</c> or <c>not-a-number</c>.</param>
public readonly record struct MalformedField(string Serializer, string Field, BsonType BsonType, string Reason);

/// <summary>
/// Receives every defect the tolerant serializers absorb. The rule is that a bad value drops that one
/// field and never the whole document, so this sink is how the dropped data stays visible.
/// </summary>
public interface IMalformedFieldSink
{
    void Report(MalformedField defect);
}

/// <summary>A sink that ignores everything; used when a serializer is constructed without a sink.</summary>
public sealed class NullMalformedFieldSink : IMalformedFieldSink
{
    public static NullMalformedFieldSink Instance { get; } = new();

    private NullMalformedFieldSink()
    {
    }

    public void Report(MalformedField defect)
    {
    }
}
