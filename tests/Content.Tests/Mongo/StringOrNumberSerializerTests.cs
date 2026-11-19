using Content.Mongo.Serialization;
using Content.Tests.Support;
using MongoDB.Bson;

namespace Content.Tests.Mongo;

public class StringOrNumberSerializerTests
{
    private readonly RecordingSink _sink = new();

    private StringOrNumberSerializer Serializer => new(_sink, "Code");

    [Fact]
    public void Reads_a_string_unchanged()
    {
        SerializerHarness.Read(Serializer, new BsonString("AB-12")).ShouldBe("AB-12");
        _sink.Defects.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(-7, "-7")]
    public void Reads_int32_as_invariant_string(int number, string expected) =>
        SerializerHarness.Read(Serializer, new BsonInt32(number)).ShouldBe(expected);

    [Fact]
    public void Reads_int64_beyond_double_precision_without_loss() =>
        SerializerHarness.Read(Serializer, new BsonInt64(9_007_199_254_740_993L)).ShouldBe("9007199254740993");

    [Theory]
    [InlineData(12345.0, "12345")]
    [InlineData(3.5, "3.5")]
    public void Reads_double_with_invariant_formatting(double number, string expected) =>
        SerializerHarness.Read(Serializer, new BsonDouble(number)).ShouldBe(expected);

    [Fact]
    public void Reads_decimal128() =>
        SerializerHarness.Read(Serializer, new BsonDecimal128(Decimal128.Parse("19.95"))).ShouldBe("19.95");

    [Fact]
    public void Null_is_null_and_not_a_defect()
    {
        SerializerHarness.Read(Serializer, BsonNull.Value).ShouldBeNull();
        _sink.Defects.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public void Drops_unsupported_values_and_reports_them(BsonValue value)
    {
        SerializerHarness.Read(Serializer, value).ShouldBeNull();

        var defect = _sink.Defects.ShouldHaveSingleItem();
        defect.Field.ShouldBe("Code");
        defect.Serializer.ShouldBe(nameof(StringOrNumberSerializer));
        defect.Reason.ShouldBe("unsupported-type");
    }

    public static TheoryData<BsonValue> UnsupportedValues() =>
    [
        new BsonDocument("a", 1),
        new BsonArray { 1, 2 },
        BsonBoolean.True,
        new BsonObjectId(ObjectId.GenerateNewId()),
    ];

    [Fact]
    public void Serializes_a_string_and_null()
    {
        SerializerHarness.Write(Serializer, "x").ShouldBe(new BsonString("x"));
        SerializerHarness.Write(Serializer, null).ShouldBe(BsonNull.Value);
    }
}
