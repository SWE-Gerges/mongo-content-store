using Content.Store.Models;
using Content.Store.Rooms;
using MongoDB.Bson;

namespace Content.Tests.Store;

public class RoomExtraMapperTests
{
    private static RoomData Room(BsonDocument? extra = null, RoomExtraFields? fields = null, object? groupId = null) => new()
    {
        Id = "R1",
        Name = "Double room",
        GroupIdRaw = groupId,
        Extra = extra,
        ExtraFields = fields,
    };

    public class SupplierA
    {
        private readonly SupplierARoomExtraMapper _mapper = new();

        [Fact]
        public void Reads_pax_room_type_and_codes_from_the_free_form_extra()
        {
            var extra = new BsonDocument { { "Pax", 2 }, { "RoomType", "DBL" }, { "CharacteristicCodes", new BsonArray { "BA", "VW" } } };

            var detail = _mapper.Map(Room(extra, groupId: 7));

            detail.RoomId.ShouldBe("R1");
            detail.Name.ShouldBe("Double room");
            detail.GroupId.ShouldBe("7");
            detail.Occupancy.ShouldBe(2);
            detail.RoomType.ShouldBe("DBL");
            detail.CharacteristicCodes.ShouldBe(["BA", "VW"]);
            detail.RoomClass.ShouldBeNull();
        }

        [Fact]
        public void Extra_keys_are_matched_case_insensitively_and_text_numbers_are_parsed()
        {
            var extra = new BsonDocument { { "pax", "3" }, { "roomtype", "TPL" } };

            var detail = _mapper.Map(Room(extra));

            detail.Occupancy.ShouldBe(3);
            detail.RoomType.ShouldBe("TPL");
        }

        [Fact]
        public void Falls_back_to_the_typed_fields_when_extra_has_nothing()
        {
            var fields = new RoomExtraFields { Pax = "4", RoomType = "QUAD", CharacteristicCodes = ["BL"] };

            var detail = _mapper.Map(Room(fields: fields));

            detail.Occupancy.ShouldBe(4);
            detail.RoomType.ShouldBe("QUAD");
            detail.CharacteristicCodes.ShouldBe(["BL"]);
        }

        [Fact]
        public void Extra_wins_over_the_typed_fields_per_value()
        {
            var extra = new BsonDocument { { "Pax", 2 } };
            var fields = new RoomExtraFields { Pax = "9", RoomType = "SGL" };

            var detail = _mapper.Map(Room(extra, fields));

            detail.Occupancy.ShouldBe(2);
            detail.RoomType.ShouldBe("SGL");
        }

        [Fact]
        public void Tolerates_wrong_types_and_missing_data()
        {
            var extra = new BsonDocument
            {
                { "Pax", new BsonDocument("x", 1) },
                { "RoomType", new BsonArray { "DBL" } },
                { "CharacteristicCodes", new BsonArray { "BA", new BsonDocument("x", 1), 5, BsonNull.Value } },
            };

            var detail = _mapper.Map(Room(extra));

            detail.Occupancy.ShouldBeNull();
            detail.RoomType.ShouldBeNull();
            detail.CharacteristicCodes.ShouldBe(["BA", "5"]);
        }

        [Theory]
        [InlineData("many")]
        [InlineData("-2")]
        [InlineData("")]
        public void Unusable_pax_text_is_absent_not_an_error(string pax) =>
            _mapper.Map(Room(new BsonDocument("Pax", pax))).Occupancy.ShouldBeNull();

        [Fact]
        public void A_room_with_no_extras_maps_to_an_empty_detail()
        {
            var detail = _mapper.Map(Room());

            detail.Occupancy.ShouldBeNull();
            detail.CharacteristicCodes.ShouldBeEmpty();
        }

        [Fact]
        public void Declares_only_its_own_supplier() =>
            _mapper.SupplierKeys.ShouldBe(["supplier-a"]);
    }

    public class SupplierB
    {
        private readonly SupplierBRoomExtraMapper _mapper = new();

        [Fact]
        public void Reads_class_quality_bedding_and_bathroom()
        {
            var extra = new BsonDocument { { "Class", "STD" }, { "Quality", "Comfort" }, { "Bedding", "King" }, { "Bathroom", "Shower" } };

            var detail = _mapper.Map(Room(extra, groupId: "G-1"));

            detail.GroupId.ShouldBe("G-1");
            detail.RoomClass.ShouldBe("STD");
            detail.Quality.ShouldBe("Comfort");
            detail.Bedding.ShouldBe("King");
            detail.Bathroom.ShouldBe("Shower");
            detail.Occupancy.ShouldBeNull();
        }

        [Fact]
        public void Mixes_extra_and_typed_fields_per_attribute()
        {
            var extra = new BsonDocument { { "Class", "DLX" } };
            var fields = new RoomExtraFields { Class = "STD", Bedding = "Twin", Bathroom = "Bath" };

            var detail = _mapper.Map(Room(extra, fields));

            detail.RoomClass.ShouldBe("DLX");
            detail.Bedding.ShouldBe("Twin");
            detail.Bathroom.ShouldBe("Bath");
            detail.Quality.ShouldBeNull();
        }

        [Fact]
        public void Tolerates_numeric_and_malformed_values()
        {
            var extra = new BsonDocument { { "Class", 3 }, { "Quality", new BsonDocument("x", 1) }, { "Bedding", "  " } };

            var detail = _mapper.Map(Room(extra));

            detail.RoomClass.ShouldBe("3");
            detail.Quality.ShouldBeNull();
            detail.Bedding.ShouldBeNull();
        }
    }
}

public class RoomExtraMapperRegistryTests
{
    private sealed class FakeMapper(params string[] suppliers) : IRoomExtraMapper
    {
        public IReadOnlyCollection<string> SupplierKeys { get; } = suppliers;

        public RoomDetail Map(RoomData room) =>
            new(room.Id, room.Name, null, null, null, null, null, null, null, []);
    }

    private static readonly RoomData Sample = new() { Id = "R1", Name = "Room" };

    [Fact]
    public void Routes_each_supplier_to_the_mapper_that_declared_it()
    {
        var registry = new RoomExtraMapperRegistry([new SupplierARoomExtraMapper(), new SupplierBRoomExtraMapper()]);

        registry.TryGetMapper("supplier-a", out var a).ShouldBeTrue();
        registry.TryGetMapper("supplier-b", out var b).ShouldBeTrue();

        a.ShouldBeOfType<SupplierARoomExtraMapper>();
        b.ShouldBeOfType<SupplierBRoomExtraMapper>();
        registry.SupportedSuppliers.ShouldBe(["supplier-a", "supplier-b"], ignoreOrder: true);
    }

    [Fact]
    public void An_unknown_supplier_is_an_explicit_no_mapper_result_not_an_exception()
    {
        var registry = new RoomExtraMapperRegistry([new SupplierARoomExtraMapper()]);

        var result = registry.Map("supplier-c", Sample);

        result.HasMapper.ShouldBeFalse();
        result.Detail.ShouldBeNull();
        registry.TryGetMapper("supplier-c", out var mapper).ShouldBeFalse();
        mapper.ShouldBeNull();
    }

    [Fact]
    public void A_known_supplier_returns_the_mapped_detail()
    {
        var registry = new RoomExtraMapperRegistry([new SupplierARoomExtraMapper()]);

        var result = registry.Map("supplier-a", new RoomData { Id = "R9", Name = "Suite", Extra = new BsonDocument("Pax", 2) });

        result.HasMapper.ShouldBeTrue();
        result.Detail!.RoomId.ShouldBe("R9");
        result.Detail.Occupancy.ShouldBe(2);
    }

    [Fact]
    public void A_mapper_can_serve_several_suppliers()
    {
        var registry = new RoomExtraMapperRegistry([new FakeMapper("supplier-x", "supplier-y")]);

        registry.Map("supplier-x", Sample).HasMapper.ShouldBeTrue();
        registry.Map("supplier-y", Sample).HasMapper.ShouldBeTrue();
    }

    [Fact]
    public void Two_mappers_claiming_one_supplier_is_a_startup_error() =>
        Should.Throw<InvalidOperationException>(() =>
                new RoomExtraMapperRegistry([new FakeMapper("supplier-a"), new SupplierARoomExtraMapper()]))
            .Message.ShouldContain("supplier-a");

    [Fact]
    public void Supplier_keys_are_case_sensitive_plain_strings()
    {
        var registry = new RoomExtraMapperRegistry([new SupplierARoomExtraMapper()]);

        registry.Map("SUPPLIER-A", Sample).HasMapper.ShouldBeFalse();
    }
}
