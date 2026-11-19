using Content.Mongo.Serialization;
using Content.Store.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Content.Store;

/// <summary>
/// Registers the class maps that bind each tolerant serializer to the document members it protects.
/// The driver's serializer registry is process-wide, so registration happens once per process and the sink
/// that was bound first stays bound; <see cref="EnsureRegistered"/> returns that sink so dependency injection
/// exposes the same instance the serializers report to.
/// </summary>
public static class HotelBsonMappings
{
    private static readonly Lock Gate = new();
    private static IMalformedFieldSink? s_registeredSink;

    public static IMalformedFieldSink EnsureRegistered(IMalformedFieldSink candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (Gate)
        {
            if (s_registeredSink is not null)
            {
                return s_registeredSink;
            }

            Register(candidate);
            s_registeredSink = candidate;
            return candidate;
        }
    }

    private static void Register(IMalformedFieldSink sink)
    {
        BsonClassMap.RegisterClassMap<HotelDocument>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
            map.GetMemberMap(h => h.Code).SetSerializer(Text(sink, nameof(HotelDocument.Code)));
            map.GetMemberMap(h => h.CanonicalId).SetSerializer(Text(sink, nameof(HotelDocument.CanonicalId)));
            map.GetMemberMap(h => h.Name).SetSerializer(Text(sink, nameof(HotelDocument.Name)));
            map.GetMemberMap(h => h.Names).SetSerializer(new TolerantStringMapSerializer(sink, nameof(HotelDocument.Names)));
            map.GetMemberMap(h => h.CityCode).SetSerializer(Text(sink, nameof(HotelDocument.CityCode)));
            map.GetMemberMap(h => h.CountryCode).SetSerializer(Text(sink, nameof(HotelDocument.CountryCode)));
            map.GetMemberMap(h => h.Rating).SetSerializer(new TolerantDoubleSerializer(sink, nameof(HotelDocument.Rating)));
            map.GetMemberMap(h => h.Address).SetSerializer(Text(sink, nameof(HotelDocument.Address)));
            map.GetMemberMap(h => h.Latitude).SetSerializer(new TolerantNullableDoubleSerializer(sink, nameof(HotelDocument.Latitude)));
            map.GetMemberMap(h => h.Longitude).SetSerializer(new TolerantNullableDoubleSerializer(sink, nameof(HotelDocument.Longitude)));
            map.GetMemberMap(h => h.Images).SetSerializer(new ImageUrlListSerializer(sink, nameof(HotelDocument.Images)));
            map.GetMemberMap(h => h.Amenities).SetSerializer(new TolerantStringListSerializer(sink, nameof(HotelDocument.Amenities)));
            map.GetMemberMap(h => h.Rooms).SetSerializer(new TolerantDocumentListSerializer<RoomData>(sink, nameof(HotelDocument.Rooms)));
            map.GetMemberMap(h => h.Providers).SetSerializer(new TolerantDocumentListSerializer<ProviderRef>(sink, nameof(HotelDocument.Providers)));
        });

        BsonClassMap.RegisterClassMap<RoomData>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
            map.GetMemberMap(r => r.Id).SetSerializer(Text(sink, "Room.Id"));
            map.GetMemberMap(r => r.GroupIdRaw).SetSerializer(new ScalarObjectSerializer(sink, "Room.GroupId"));
            map.GetMemberMap(r => r.Name).SetSerializer(Text(sink, "Room.Name"));
            map.GetMemberMap(r => r.Extra).SetSerializer(new TolerantDocumentSerializer<BsonDocument>(sink, "Room.Extra"));
            map.GetMemberMap(r => r.ExtraFields).SetSerializer(new TolerantDocumentSerializer<RoomExtraFields>(sink, "Room.ExtraFields"));
        });

        BsonClassMap.RegisterClassMap<RoomExtraFields>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
            map.GetMemberMap(r => r.Pax).SetSerializer(Text(sink, "Room.ExtraFields.Pax"));
            map.GetMemberMap(r => r.RoomType).SetSerializer(Text(sink, "Room.ExtraFields.RoomType"));
            map.GetMemberMap(r => r.CharacteristicCodes).SetSerializer(new TolerantStringListSerializer(sink, "Room.ExtraFields.CharacteristicCodes"));
            map.GetMemberMap(r => r.Class).SetSerializer(Text(sink, "Room.ExtraFields.Class"));
            map.GetMemberMap(r => r.Quality).SetSerializer(Text(sink, "Room.ExtraFields.Quality"));
            map.GetMemberMap(r => r.Bedding).SetSerializer(Text(sink, "Room.ExtraFields.Bedding"));
            map.GetMemberMap(r => r.Bathroom).SetSerializer(Text(sink, "Room.ExtraFields.Bathroom"));
        });

        BsonClassMap.RegisterClassMap<ProviderRef>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
            map.GetMemberMap(p => p.Supplier).SetSerializer(Text(sink, "Provider.Supplier"));
            map.GetMemberMap(p => p.HotelId).SetSerializer(Text(sink, "Provider.HotelId"));
        });
    }

    private static StringOrNumberSerializer Text(IMalformedFieldSink sink, string field) => new(sink, field);
}
