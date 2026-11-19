using Content.Store.Models;
using Content.Tests.Support;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Content.Tests.Mongo;

[Collection(ClassMapCollection.Name)]
public class HotelDocumentDecodingTests
{
    private static BsonDocument CleanHotel() => new()
    {
        { "_id", 42L },
        { "Code", "AB12" },
        { "CanonicalId", "c-42" },
        { "Name", "Grand Example" },
        { "Names", new BsonDocument { { "en", "Grand Example" }, { "xx", "Grand Exemple" } } },
        { "CityCode", "C001" },
        { "CountryCode", "XX" },
        { "Rating", 4.5 },
        { "Address", "1 Example Street" },
        { "Latitude", 10.5 },
        { "Longitude", 20.25 },
        { "Images", new BsonArray { "https://example.com/1.jpg", new BsonDocument("url", "https://example.com/2.jpg") } },
        { "Amenities", new BsonArray { "wifi", "pool" } },
        {
            "Rooms", new BsonArray
            {
                new BsonDocument { { "Id", 1 }, { "GroupId", 7 }, { "Name", "Double" }, { "Extra", new BsonDocument("Pax", 2) } },
                new BsonDocument { { "Id", "R2" }, { "GroupId", "G-9" }, { "Name", "Suite" } },
            }
        },
        {
            "Providers", new BsonArray
            {
                new BsonDocument { { "Supplier", "supplier-a" }, { "HotelId", 1001 } },
                new BsonDocument { { "Supplier", "supplier-b" }, { "HotelId", "B-77" } },
            }
        },
        { "SomethingNew", "ignored extra element" },
    };

    private static HotelDocument Decode(BsonDocument document)
    {
        _ = TestMappings.Counter;
        return BsonSerializer.Deserialize<HotelDocument>(document);
    }

    [Fact]
    public void A_clean_document_decodes_completely_and_ignores_extra_elements()
    {
        var before = TestMappings.Counter.Total;

        var hotel = Decode(CleanHotel());

        hotel.Id.ShouldBe(42L);
        hotel.Code.ShouldBe("AB12");
        hotel.Name.ShouldBe("Grand Example");
        hotel.Names["xx"].ShouldBe("Grand Exemple");
        hotel.Rating.ShouldBe(4.5);
        hotel.Latitude.ShouldBe(10.5);
        hotel.Images.ShouldBe(["https://example.com/1.jpg", "https://example.com/2.jpg"]);
        hotel.Amenities.ShouldBe(["wifi", "pool"]);
        hotel.Rooms.Select(r => r.Id).ShouldBe(["1", "R2"]);
        hotel.Rooms.Select(r => r.GroupId).ShouldBe(["7", "G-9"]);
        hotel.Rooms[0].GroupIdRaw.ShouldBe(7);
        hotel.Rooms[1].GroupIdRaw.ShouldBe("G-9");
        hotel.Providers.Select(p => p.HotelId).ShouldBe(["1001", "B-77"]);
        TestMappings.Counter.Total.ShouldBe(before);
    }

    [Fact]
    public void A_document_with_every_defect_decodes_with_only_the_bad_fields_dropped()
    {
        var document = CleanHotel();
        document["Code"] = new BsonDocument("oops", 1);
        document["Names"] = new BsonArray { "not", "a map" };
        document["Rating"] = "five stars";
        document["Latitude"] = new BsonDocument("deg", 1);
        document["Images"] = new BsonArray { new BsonDocument("caption", "no url"), "https://example.com/ok.jpg" };
        document["Amenities"] = new BsonDocument { { "0", "wifi" } };
        document["Rooms"] = new BsonArray
        {
            new BsonDocument { { "Id", 1 }, { "GroupId", new BsonDocument("x", 1) }, { "Name", "Double" }, { "Extra", "not a document" }, { "ExtraFields", new BsonArray { 1 } } },
            "stray",
            new BsonDocument { { "Id", 2 }, { "Name", "Suite" } },
        };
        document["Providers"] = new BsonArray { new BsonDocument { { "Supplier", "supplier-a" }, { "HotelId", new BsonArray { 1 } } } };
        var before = TestMappings.Counter.Total;

        var hotel = Decode(document);

        // Everything that was readable survives.
        hotel.Id.ShouldBe(42L);
        hotel.Name.ShouldBe("Grand Example");
        hotel.CityCode.ShouldBe("C001");
        hotel.Longitude.ShouldBe(20.25);
        hotel.Images.ShouldBe(["https://example.com/ok.jpg"]);

        // Only the bad fields were dropped.
        hotel.Code.ShouldBeNull();
        hotel.Names.ShouldBeEmpty();
        hotel.Rating.ShouldBe(0);
        hotel.Latitude.ShouldBeNull();
        hotel.Amenities.ShouldBeEmpty();
        hotel.Rooms.Count.ShouldBe(2);
        hotel.Rooms[0].GroupIdRaw.ShouldBeNull();
        hotel.Rooms[0].Extra.ShouldBeNull();
        hotel.Rooms[0].ExtraFields.ShouldBeNull();
        hotel.Rooms[0].Name.ShouldBe("Double");
        hotel.Rooms[1].Name.ShouldBe("Suite");
        hotel.Providers.Single().Supplier.ShouldBe("supplier-a");
        hotel.Providers.Single().HotelId.ShouldBeNull();

        // And the defects stayed visible.
        (TestMappings.Counter.Total - before).ShouldBeGreaterThanOrEqualTo(10);
        TestMappings.Counter.Count("Rating", "not-a-number").ShouldBeGreaterThanOrEqualTo(1);
        TestMappings.Counter.Count("Code", "unsupported-type").ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Round_trips_through_the_same_mappings()
    {
        var hotel = Decode(CleanHotel());

        var written = hotel.ToBsonDocument();
        var again = BsonSerializer.Deserialize<HotelDocument>(written);

        again.Id.ShouldBe(hotel.Id);
        again.Rooms.Select(r => r.GroupIdRaw).ShouldBe(hotel.Rooms.Select(r => r.GroupIdRaw));
        again.Providers.Select(p => p.HotelId).ShouldBe(hotel.Providers.Select(p => p.HotelId));
        again.Images.ShouldBe(hotel.Images);
        written["_id"].ShouldBe(new BsonInt64(42));
    }
}
