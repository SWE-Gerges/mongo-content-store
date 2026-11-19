using Content.Mongo.Serialization;
using Content.Tests.Support;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Content.Tests.Mongo;

public class ScalarObjectSerializerTests
{
    private readonly RecordingSink _sink = new();

    private ScalarObjectSerializer Serializer => new(_sink, "Room.GroupId");

    [Fact]
    public void Preserves_the_stored_scalar_type()
    {
        SerializerHarness.Read(Serializer, new BsonInt32(7)).ShouldBe(7);
        SerializerHarness.Read(Serializer, new BsonInt64(8)).ShouldBe(8L);
        SerializerHarness.Read(Serializer, new BsonString("G-9")).ShouldBe("G-9");
        SerializerHarness.Read(Serializer, new BsonDouble(1.5)).ShouldBe(1.5);
        SerializerHarness.Read(Serializer, BsonBoolean.True).ShouldBe(true);
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void A_sub_document_is_dropped_instead_of_throwing()
    {
        SerializerHarness.Read(Serializer, new BsonDocument("a", 1)).ShouldBeNull();
        SerializerHarness.Read(Serializer, new BsonArray { 1 }).ShouldBeNull();

        _sink.Defects.Count.ShouldBe(2);
        _sink.Defects.ShouldAllBe(d => d.Field == "Room.GroupId" && d.Reason == "unsupported-type");
    }

    [Fact]
    public void Round_trips_int_string_and_null()
    {
        SerializerHarness.Write(Serializer, 5).ShouldBe(new BsonInt32(5));
        SerializerHarness.Write(Serializer, "x").ShouldBe(new BsonString("x"));
        SerializerHarness.Write(Serializer, null).ShouldBe(BsonNull.Value);
    }

    [Fact]
    public void Refuses_to_write_a_type_it_cannot_represent() =>
        Should.Throw<MongoDB.Bson.BsonSerializationException>(() => SerializerHarness.Write(Serializer, new object()));
}

public class TolerantStringMapSerializerTests
{
    private readonly RecordingSink _sink = new();

    private TolerantStringMapSerializer Serializer => new(_sink, "Names");

    [Fact]
    public void Reads_a_language_map()
    {
        var map = SerializerHarness.Read(Serializer, new BsonDocument { { "en", "Grand Hotel" }, { "xx", "Grand Hotel XX" } });

        map.ShouldBe(new Dictionary<string, string> { ["en"] = "Grand Hotel", ["xx"] = "Grand Hotel XX" });
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Drops_entries_that_are_not_text_and_keeps_the_rest()
    {
        var map = SerializerHarness.Read(Serializer, new BsonDocument { { "en", "Grand Hotel" }, { "bad", new BsonDocument("x", 1) }, { "n", 5 }, { "empty", "" } });

        map.ShouldBe(new Dictionary<string, string> { ["en"] = "Grand Hotel", ["n"] = "5" });
        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe("unsupported-entry");
    }

    [Theory]
    [InlineData("a string")]
    public void A_non_document_gives_an_empty_map_and_a_defect(string stored)
    {
        SerializerHarness.Read(Serializer, new BsonString(stored)).ShouldBeEmpty();
        SerializerHarness.Read(Serializer, new BsonArray { "a" }).ShouldBeEmpty();

        _sink.Defects.Count.ShouldBe(2);
    }

    [Fact]
    public void Round_trips() =>
        SerializerHarness.Write(Serializer, new Dictionary<string, string> { ["en"] = "A" }).ShouldBe(new BsonDocument("en", "A"));
}

public class TolerantDocumentSerializerTests
{
    private readonly RecordingSink _sink = new();

    [BsonIgnoreExtraElements]
    public sealed class Item
    {
        public int Number { get; set; }
    }

    [Fact]
    public void Maps_good_elements_and_drops_the_unmappable_ones()
    {
        var serializer = new TolerantDocumentListSerializer<Item>(_sink, "Items");
        var value = new BsonArray
        {
            new BsonDocument("Number", 1),
            new BsonDocument("Number", "not a number"),
            "stray string",
            BsonNull.Value,
            new BsonDocument("Number", 3),
        };

        var items = SerializerHarness.Read(serializer, value);

        items.Select(i => i.Number).ShouldBe([1, 3]);
        _sink.Defects.Select(d => d.Reason).ShouldBe(["unmappable-document", "unsupported-element"]);
    }

    [Fact]
    public void A_single_unmappable_document_becomes_null()
    {
        var serializer = new TolerantDocumentSerializer<Item>(_sink, "Item");

        SerializerHarness.Read(serializer, new BsonDocument("Number", "x")).ShouldBeNull();
        SerializerHarness.Read(serializer, new BsonString("x")).ShouldBeNull();
        SerializerHarness.Read(serializer, new BsonDocument("Number", 4))!.Number.ShouldBe(4);

        _sink.Defects.Count.ShouldBe(2);
    }

    [Fact]
    public void A_non_array_value_gives_an_empty_list()
    {
        var serializer = new TolerantDocumentListSerializer<Item>(_sink, "Items");

        SerializerHarness.Read(serializer, new BsonString("x")).ShouldBeEmpty();
        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe("unsupported-type");
    }
}
