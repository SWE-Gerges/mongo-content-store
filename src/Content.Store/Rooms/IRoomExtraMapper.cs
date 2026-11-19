using Content.Store.Models;

namespace Content.Store.Rooms;

/// <summary>
/// Strategy that turns a supplier's room data (free-form <c>Extra</c> plus the typed <c>ExtraFields</c>) into the
/// supplier-neutral <see cref="RoomDetail"/>. A mapper declares the supplier keys it handles; the
/// <see cref="RoomExtraMapperRegistry"/> is the only place that decides which mapper serves which supplier.
/// </summary>
public interface IRoomExtraMapper
{
    IReadOnlyCollection<string> SupplierKeys { get; }

    RoomDetail Map(RoomData room);
}
