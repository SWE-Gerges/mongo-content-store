using Content.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Content.Tests.Support;

internal sealed class RecordingSink : IMalformedFieldSink
{
    public List<MalformedField> Defects { get; } = [];

    public void Report(MalformedField defect) => Defects.Add(defect);
}

internal static class SerializerHarness
{
    /// <summary>Deserializes one value; the closing ReadEndDocument proves the serializer consumed exactly that value.</summary>
    public static T Read<T>(IBsonSerializer<T> serializer, BsonValue value)
    {
        var document = new BsonDocument("v", value);
        using var reader = new BsonDocumentReader(document);
        reader.ReadStartDocument();
        reader.ReadBsonType();
        reader.ReadName();
        var result = serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));
        reader.ReadEndDocument();
        return result;
    }

    public static BsonValue Write<T>(IBsonSerializer<T> serializer, T value)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        writer.WriteStartDocument();
        writer.WriteName("v");
        serializer.Serialize(BsonSerializationContext.CreateRoot(writer), value);
        writer.WriteEndDocument();
        return document["v"];
    }
}
