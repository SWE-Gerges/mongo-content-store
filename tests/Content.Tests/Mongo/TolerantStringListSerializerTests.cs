using Content.Mongo.Serialization;
using Content.Tests.Support;
using MongoDB.Bson;

namespace Content.Tests.Mongo;

public class TolerantStringListSerializerTests
{
    private readonly RecordingSink _sink = new();

    private TolerantStringListSerializer Serializer => new(_sink, "Amenities");

    [Fact]
    public void Reads_an_array_of_strings()
    {
        SerializerHarness.Read(Serializer, new BsonArray { "wifi", "pool" }).ShouldBe(["wifi", "pool"]);
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_a_single_string_as_one_element()
    {
        SerializerHarness.Read(Serializer, new BsonString("wifi")).ShouldBe(["wifi"]);
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Stringifies_numbers_inside_the_array() =>
        SerializerHarness.Read(Serializer, new BsonArray { "a", 7, 2.5 }).ShouldBe(["a", "7", "2.5"]);

    [Fact]
    public void Keeps_the_good_elements_of_a_partly_broken_array_and_reports_the_rest()
    {
        var value = new BsonArray { "wifi", new BsonDocument("x", 1), "pool", new BsonArray { "nested" }, BsonNull.Value, "spa" };

        SerializerHarness.Read(Serializer, value).ShouldBe(["wifi", "pool", "spa"]);

        _sink.Defects.Count.ShouldBe(2);
        _sink.Defects.ShouldAllBe(d => d.Reason == "unsupported-element" && d.Field == "Amenities");
    }

    [Fact]
    public void A_malformed_sub_document_gives_an_empty_list_and_a_defect()
    {
        SerializerHarness.Read(Serializer, new BsonDocument { { "0", "wifi" }, { "1", "pool" } }).ShouldBeEmpty();

        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe("unsupported-type");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_strings_are_ignored(string blank) =>
        SerializerHarness.Read(Serializer, new BsonString(blank)).ShouldBeEmpty();

    [Fact]
    public void Null_gives_an_empty_list_without_a_defect()
    {
        SerializerHarness.Read(Serializer, BsonNull.Value).ShouldBeEmpty();
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Writes_a_plain_array() =>
        SerializerHarness.Write(Serializer, ["a", "b"]).ShouldBe(new BsonArray { "a", "b" });
}
