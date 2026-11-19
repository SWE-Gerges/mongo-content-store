using Content.Mongo.Serialization;
using Content.Tests.Support;
using MongoDB.Bson;

namespace Content.Tests.Mongo;

public class ImageUrlListSerializerTests
{
    private readonly RecordingSink _sink = new();

    private ImageUrlListSerializer Serializer => new(_sink, "Images");

    [Fact]
    public void Reads_an_array_of_urls()
    {
        SerializerHarness.Read(Serializer, new BsonArray { "https://example.com/a.jpg", "https://example.com/b.jpg" })
            .ShouldBe(["https://example.com/a.jpg", "https://example.com/b.jpg"]);
        _sink.Defects.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("url")]
    [InlineData("Url")]
    [InlineData("URL")]
    public void Reads_objects_with_a_url_field_in_any_casing(string fieldName)
    {
        var value = new BsonArray { new BsonDocument { { "caption", "Lobby" }, { fieldName, "https://example.com/lobby.jpg" } } };

        SerializerHarness.Read(Serializer, value).ShouldBe(["https://example.com/lobby.jpg"]);
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_a_mix_of_strings_and_objects()
    {
        var value = new BsonArray { "https://example.com/a.jpg", new BsonDocument("url", "https://example.com/b.jpg") };

        SerializerHarness.Read(Serializer, value).ShouldBe(["https://example.com/a.jpg", "https://example.com/b.jpg"]);
    }

    [Fact]
    public void An_object_without_a_url_is_dropped_and_reported()
    {
        var value = new BsonArray { new BsonDocument("caption", "no link"), "https://example.com/a.jpg" };

        SerializerHarness.Read(Serializer, value).ShouldBe(["https://example.com/a.jpg"]);
        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe("missing-url");
    }

    [Fact]
    public void A_url_field_that_is_not_a_string_counts_as_missing()
    {
        SerializerHarness.Read(Serializer, new BsonArray { new BsonDocument("url", 5) }).ShouldBeEmpty();
        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe("missing-url");
    }

    [Fact]
    public void A_lone_string_or_object_is_a_one_element_list()
    {
        SerializerHarness.Read(Serializer, new BsonString("https://example.com/a.jpg")).ShouldBe(["https://example.com/a.jpg"]);
        SerializerHarness.Read(Serializer, new BsonDocument("Url", "https://example.com/b.jpg")).ShouldBe(["https://example.com/b.jpg"]);
    }

    [Fact]
    public void Unsupported_elements_and_values_are_reported_not_thrown()
    {
        SerializerHarness.Read(Serializer, new BsonArray { 12, "https://example.com/a.jpg" }).ShouldBe(["https://example.com/a.jpg"]);
        SerializerHarness.Read(Serializer, BsonBoolean.True).ShouldBeEmpty();

        _sink.Defects.Select(d => d.Reason).ShouldBe(["unsupported-element", "unsupported-type"]);
    }

    [Fact]
    public void Writes_a_plain_array_of_strings() =>
        SerializerHarness.Write(Serializer, ["https://example.com/a.jpg"]).ShouldBe(new BsonArray { "https://example.com/a.jpg" });
}
