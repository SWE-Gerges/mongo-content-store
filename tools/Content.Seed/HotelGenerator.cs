using System.Globalization;
using Bogus;
using MongoDB.Bson;

namespace Content.Seed;

/// <summary>The messy shapes the generator can inject, named so the summary can count them.</summary>
internal static class Defect
{
    public const string RatingAsText = "rating-as-text";
    public const string ImagesWithoutUrl = "images-without-url";
    public const string AmenitiesAsObject = "amenities-as-object";
    public const string AmenitiesTruncated = "amenities-truncated";
    public const string NamesAsText = "names-as-text";
    public const string LatitudeAsText = "latitude-as-text";
    public const string CodeAsNumber = "code-as-number";
    public const string StrayRoomEntry = "stray-room-entry";
    public const string RoomExtraAsText = "room-extra-as-text";

    public static readonly string[] All =
    [
        RatingAsText, ImagesWithoutUrl, AmenitiesAsObject, AmenitiesTruncated, NamesAsText,
        LatitudeAsText, CodeAsNumber, StrayRoomEntry, RoomExtraAsText,
    ];
}

/// <summary>
/// Builds synthetic hotel documents as raw BSON, because the point is to store shapes a typed model would never
/// write. Each document is generated from its own seed (global seed, supplier, id), so the same arguments always
/// produce the same ids, codes and room ids regardless of batching or order.
/// </summary>
internal sealed class HotelGenerator(int seed, double malformedRate)
{
    private static readonly string[] Adjectives = ["Grand", "Royal", "Central", "Quiet", "Bright", "Golden", "Silver", "Northern", "Southern", "Harbor", "Garden", "Summit", "Maple", "Cedar", "Lakeside", "Urban"];
    private static readonly string[] Nouns = ["Palace", "Plaza", "Residence", "Lodge", "Inn", "Suites", "Tower", "Court", "Retreat", "House"];
    private static readonly string[] Suffixes = ["", "", "Annex", "Central", "East", "West"];
    private static readonly string[] AmenityPool = ["wifi", "pool", "spa", "gym", "parking", "breakfast", "bar", "shuttle", "pets", "laundry"];
    private static readonly string[] RoomTypes = ["SGL", "DBL", "TPL", "QUAD", "SUI"];
    private static readonly string[] CharacteristicCodes = ["BA", "VW", "BL", "KT", "AC"];
    private static readonly string[] Classes = ["STD", "DLX", "SUP"];
    private static readonly string[] Qualities = ["Economy", "Comfort", "Premium"];
    private static readonly string[] Bedding = ["Twin", "King", "Queen"];
    private static readonly string[] Bathrooms = ["Shower", "Bath"];

    public const int CityCount = 200;

    public static string Code(string supplierKey, long id) =>
        $"{char.ToUpperInvariant(supplierKey[^1])}H{id.ToString("D6", CultureInfo.InvariantCulture)}";

    public (BsonDocument Document, List<string> Defects) Supplier(string supplierKey, int supplierIndex, long id)
    {
        var random = new Randomizer(Mix(supplierIndex, id));
        var city = random.Int(1, CityCount);
        var name = HotelName(random);
        var document = new BsonDocument
        {
            { "_id", id },
            { "Code", Code(supplierKey, id) },
            { "CanonicalId", $"c-{id:D7}" },
            { "Name", name },
            { "Names", new BsonDocument { { "en", name }, { "xx", $"{name} (xx)" } } },
            { "CityCode", $"C{city:D3}" },
            { "CountryCode", $"X{city % 10}" },
            { "Rating", Rating(random) },
            { "Address", $"{random.Int(1, 999)} Example Street" },
            { "Latitude", Math.Round(random.Double(-60, 60), 5) },
            { "Longitude", Math.Round(random.Double(-170, 170), 5) },
            { "Images", Images(random, supplierKey, id) },
            { "Amenities", new BsonArray(random.ArrayElements(AmenityPool, random.Int(2, 6))) },
            { "Rooms", Rooms(random, supplierKey, supplierIndex) },
            { "Providers", new BsonArray { Provider(supplierKey, id, random) } },
        };

        return (document, Corrupt(document, random));
    }

    /// <summary>One merged master hotel that points back at the same id in every supplier.</summary>
    public BsonDocument Master(IReadOnlyList<string> supplierKeys, long id)
    {
        var random = new Randomizer(Mix(-1, id));
        var city = random.Int(1, CityCount);
        var name = HotelName(random);
        var providers = new BsonArray();
        foreach (var key in supplierKeys)
        {
            providers.Add(Provider(key, id, random));
        }

        return new BsonDocument
        {
            { "_id", id },
            { "Code", $"M{id:D6}" },
            { "CanonicalId", $"c-{id:D7}" },
            { "Name", name },
            { "Names", new BsonDocument { { "en", name } } },
            { "CityCode", $"C{city:D3}" },
            { "CountryCode", $"X{city % 10}" },
            { "Rating", Rating(random) },
            { "Address", $"{random.Int(1, 999)} Example Street" },
            { "Latitude", Math.Round(random.Double(-60, 60), 5) },
            { "Longitude", Math.Round(random.Double(-170, 170), 5) },
            { "Images", new BsonArray { $"https://example.com/img/master/{id}/1.jpg" } },
            { "Amenities", new BsonArray(random.ArrayElements(AmenityPool, random.Int(2, 6))) },
            { "Rooms", new BsonArray() },
            { "Providers", providers },
        };
    }

    private int Mix(int supplierIndex, long id) =>
        unchecked((seed * 1_000_003) + (supplierIndex * 7_919) + (int)(id * 31) + (int)(id >> 32));

    private static string HotelName(Randomizer random) =>
        $"{random.ArrayElement(Adjectives)} {random.ArrayElement(Nouns)} {random.ArrayElement(Suffixes)}".Trim();

    private static BsonValue Rating(Randomizer random)
    {
        var halves = random.Int(0, 10);
        var rating = halves / 2.0;

        // Suppliers store whole ratings as plain integers about a third of the time.
        return rating % 1 == 0 && random.Bool(0.35f) ? new BsonInt32((int)rating) : new BsonDouble(rating);
    }

    private static BsonArray Images(Randomizer random, string supplierKey, long id)
    {
        var images = new BsonArray();
        var count = random.Int(1, 5);
        for (var n = 1; n <= count; n++)
        {
            var url = $"https://example.com/img/{supplierKey}/{id}/{n}.jpg";
            images.Add(random.Bool(0.3f) ? new BsonDocument { { "url", url }, { "caption", $"View {n}" } } : url);
        }

        return images;
    }

    private static BsonDocument Provider(string supplierKey, long id, Randomizer random) => new()
    {
        { "Supplier", supplierKey },

        // The supplier's own id is a number in some documents and text in others.
        { "HotelId", random.Bool() ? new BsonInt64(id) : new BsonString(id.ToString(CultureInfo.InvariantCulture)) },
    };

    private static BsonArray Rooms(Randomizer random, string supplierKey, int supplierIndex)
    {
        var rooms = new BsonArray();
        var count = random.Int(2, 6);
        for (var n = 1; n <= count; n++)
        {
            var group = random.Int(1, 3);
            var room = new BsonDocument
            {
                { "Id", supplierIndex % 2 == 0 ? new BsonInt32(n) : new BsonString($"R{n}") },
                { "GroupId", random.Bool() ? new BsonInt32(group) : new BsonString($"G-{group}") },
                { "Name", $"{random.ArrayElement(RoomTypes)} room {n}" },
            };

            var attributes = RoomAttributes(random, supplierKey);
            room[random.Bool(0.25f) ? "ExtraFields" : "Extra"] = attributes;
            rooms.Add(room);
        }

        return rooms;
    }

    private static BsonDocument RoomAttributes(Randomizer random, string supplierKey) => supplierKey switch
    {
        "supplier-a" => new BsonDocument
        {
            { "Pax", random.Bool(0.2f) ? new BsonString(random.Int(1, 4).ToString(CultureInfo.InvariantCulture)) : new BsonInt32(random.Int(1, 4)) },
            { "RoomType", random.ArrayElement(RoomTypes) },
            { "CharacteristicCodes", new BsonArray(random.ArrayElements(CharacteristicCodes, random.Int(0, 3))) },
        },
        "supplier-b" => new BsonDocument
        {
            { "Class", random.ArrayElement(Classes) },
            { "Quality", random.ArrayElement(Qualities) },
            { "Bedding", random.ArrayElement(Bedding) },
            { "Bathroom", random.ArrayElement(Bathrooms) },
        },
        _ => new BsonDocument("Note", "supplier specific"),
    };

    private List<string> Corrupt(BsonDocument document, Randomizer random)
    {
        var applied = new List<string>();
        if (malformedRate <= 0 || random.Double() >= malformedRate)
        {
            return applied;
        }

        var wanted = random.Int(1, 3);
        foreach (var kind in random.ArrayElements(Defect.All, wanted))
        {
            Apply(kind, document, random);
            applied.Add(kind);
        }

        return applied;
    }

    private static void Apply(string kind, BsonDocument document, Randomizer random)
    {
        switch (kind)
        {
            case Defect.RatingAsText:
                document["Rating"] = random.Bool() ? "four stars" : document["Rating"].ToDouble().ToString("0.0", CultureInfo.InvariantCulture);
                break;
            case Defect.ImagesWithoutUrl:
                document["Images"] = new BsonArray { new BsonDocument("caption", "no link"), new BsonDocument("Url", "https://example.com/ok.jpg"), 17 };
                break;
            case Defect.AmenitiesAsObject:
                document["Amenities"] = new BsonDocument { { "0", "wifi" }, { "1", "pool" } };
                break;
            case Defect.AmenitiesTruncated:
                document["Amenities"] = new BsonArray { "wifi", BsonNull.Value };
                break;
            case Defect.NamesAsText:
                document["Names"] = document["Name"];
                break;
            case Defect.LatitudeAsText:
                document["Latitude"] = "north-ish";
                break;
            case Defect.CodeAsNumber:
                document["Code"] = random.Int(100000, 999999);
                break;
            case Defect.StrayRoomEntry:
                document["Rooms"].AsBsonArray.Insert(0, "unexpected string in rooms");
                break;
            case Defect.RoomExtraAsText:
                var rooms = document["Rooms"].AsBsonArray;
                if (rooms.Count > 0 && rooms[^1].IsBsonDocument)
                {
                    var room = rooms[^1].AsBsonDocument;
                    room.Remove("ExtraFields");
                    room["Extra"] = "see notes";
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown defect.");
        }
    }
}
