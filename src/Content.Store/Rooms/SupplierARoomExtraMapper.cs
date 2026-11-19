using Content.Store.Models;

namespace Content.Store.Rooms;

/// <summary>Reads occupancy (<c>Pax</c>), room type and characteristic codes.</summary>
public sealed class SupplierARoomExtraMapper : IRoomExtraMapper
{
    public const string SupplierKey = "supplier-a";

    public IReadOnlyCollection<string> SupplierKeys { get; } = [SupplierKey];

    public RoomDetail Map(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var reader = new RoomAttributeReader(room);

        return new RoomDetail(
            room.Id,
            room.Name,
            room.GroupId,
            Occupancy: reader.GetInt("Pax", fields => fields.Pax),
            RoomType: reader.GetString("RoomType", fields => fields.RoomType),
            RoomClass: null,
            Quality: null,
            Bedding: null,
            Bathroom: null,
            CharacteristicCodes: reader.GetStringList("CharacteristicCodes", fields => fields.CharacteristicCodes));
    }
}
