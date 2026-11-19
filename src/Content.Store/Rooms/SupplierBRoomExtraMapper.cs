using Content.Store.Models;

namespace Content.Store.Rooms;

/// <summary>Reads room class, quality, bedding and bathroom attributes.</summary>
public sealed class SupplierBRoomExtraMapper : IRoomExtraMapper
{
    public const string SupplierKey = "supplier-b";

    public IReadOnlyCollection<string> SupplierKeys { get; } = [SupplierKey];

    public RoomDetail Map(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var reader = new RoomAttributeReader(room);

        return new RoomDetail(
            room.Id,
            room.Name,
            room.GroupId,
            Occupancy: null,
            RoomType: null,
            RoomClass: reader.GetString("Class", fields => fields.Class),
            Quality: reader.GetString("Quality", fields => fields.Quality),
            Bedding: reader.GetString("Bedding", fields => fields.Bedding),
            Bathroom: reader.GetString("Bathroom", fields => fields.Bathroom),
            CharacteristicCodes: []);
    }
}
