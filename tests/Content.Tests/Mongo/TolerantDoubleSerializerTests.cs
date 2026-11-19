using Content.Mongo.Serialization;
using Content.Tests.Support;
using MongoDB.Bson;

namespace Content.Tests.Mongo;

public class TolerantDoubleSerializerTests
{
    private readonly RecordingSink _sink = new();

    private TolerantDoubleSerializer Serializer => new(_sink, "Rating");

    private TolerantNullableDoubleSerializer NullableSerializer => new(_sink, "Latitude");

    public static TheoryData<BsonValue, double> ReadableValues() =>
        new()
        {
            { new BsonDouble(3.5), 3.5 },
            { new BsonInt32(4), 4d },
            { new BsonInt64(5), 5d },
            { new BsonString("3.5"), 3.5 },
            { new BsonString(" 4 "), 4d },
            { new BsonString("4.0"), 4d },
            { new BsonDecimal128(Decimal128.Parse("2.5")), 2.5 },
        };

    [Theory]
    [MemberData(nameof(ReadableValues))]
    public void Reads_every_numeric_representation(BsonValue stored, double expected)
    {
        SerializerHarness.Read(Serializer, stored).ShouldBe(expected);
        SerializerHarness.Read(NullableSerializer, stored).ShouldBe(expected);
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void A_whole_number_rating_stored_as_an_int_keeps_its_value() =>
        SerializerHarness.Read(Serializer, new BsonInt32(3)).ShouldBe(3d);

    public static TheoryData<BsonValue, string> UnreadableValues() =>
        new()
        {
            { new BsonString("four stars"), "not-a-number" },
            { new BsonString(""), "not-a-number" },
            { new BsonString("NaN"), "not-a-number" },
            { new BsonDouble(double.NaN), "not-finite" },
            { new BsonDouble(double.PositiveInfinity), "not-finite" },
            { new BsonDocument("v", 1), "unsupported-type" },
            { new BsonArray { 1 }, "unsupported-type" },
            { BsonBoolean.True, "unsupported-type" },
        };

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public void An_unreadable_value_yields_the_default_and_a_defect(BsonValue stored, string reason)
    {
        SerializerHarness.Read(Serializer, stored).ShouldBe(0d);

        var defect = _sink.Defects.ShouldHaveSingleItem();
        defect.Reason.ShouldBe(reason);
        defect.Field.ShouldBe("Rating");
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public void The_nullable_variant_yields_null_instead_of_zero(BsonValue stored, string reason)
    {
        SerializerHarness.Read(NullableSerializer, stored).ShouldBeNull();

        _sink.Defects.ShouldHaveSingleItem().Reason.ShouldBe(reason);
    }

    [Fact]
    public void Null_is_not_a_defect()
    {
        SerializerHarness.Read(Serializer, BsonNull.Value).ShouldBe(0d);
        SerializerHarness.Read(NullableSerializer, BsonNull.Value).ShouldBeNull();
        _sink.Defects.ShouldBeEmpty();
    }

    [Fact]
    public void Writes_doubles_and_null()
    {
        SerializerHarness.Write(Serializer, 4.5).ShouldBe(new BsonDouble(4.5));
        SerializerHarness.Write(NullableSerializer, null).ShouldBe(BsonNull.Value);
        SerializerHarness.Write(NullableSerializer, 1.25).ShouldBe(new BsonDouble(1.25));
    }
}
