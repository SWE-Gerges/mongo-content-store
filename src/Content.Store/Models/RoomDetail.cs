namespace Content.Store.Models;

/// <summary>Supplier-neutral view of a room, produced by an <c>IRoomExtraMapper</c>.</summary>
public sealed record RoomDetail(
    string? RoomId,
    string? Name,
    string? GroupId,
    int? Occupancy,
    string? RoomType,
    string? RoomClass,
    string? Quality,
    string? Bedding,
    string? Bathroom,
    IReadOnlyList<string> CharacteristicCodes);
