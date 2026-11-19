using System.Text.Json.Serialization;
using Content.Store.Models;

namespace Content.Api.Contracts;

public sealed record RoomDto(string? Id, string? GroupId, string? Name);

public sealed record ProviderDto(string? Supplier, string? HotelId);

public sealed record HotelDto(
    long Id,
    string? Code,
    string? CanonicalId,
    string? Name,
    IReadOnlyDictionary<string, string> Names,
    string? CityCode,
    string? CountryCode,
    double Rating,
    string? Address,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<string> Images,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<RoomDto> Rooms,
    IReadOnlyList<ProviderDto> Providers)
{
    public static HotelDto From(HotelDocument hotel) => new(
        hotel.Id,
        hotel.Code,
        hotel.CanonicalId,
        hotel.Name,
        hotel.Names,
        hotel.CityCode,
        hotel.CountryCode,
        hotel.Rating,
        hotel.Address,
        hotel.Latitude,
        hotel.Longitude,
        hotel.Images,
        hotel.Amenities,
        hotel.Rooms.Select(r => new RoomDto(r.Id, r.GroupId, r.Name)).ToList(),
        hotel.Providers.Select(p => new ProviderDto(p.Supplier, p.HotelId)).ToList());
}

/// <summary>
/// Answer of <c>GET /content/{supplier}/hotels</c>. Looking up by <c>ids</c> fills <see cref="Items"/>; paging a city
/// with <c>cityCode</c> fills <see cref="Ids"/> and, while more pages remain, <see cref="NextCursor"/>.
/// </summary>
public sealed record HotelCollectionResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HotelDto>? Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<long>? Ids,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

public sealed record HotelSummaryDto(long Id, string? Code, string? Name, string? CityCode, string? CountryCode, double Rating)
{
    public static HotelSummaryDto From(HotelSummary summary) =>
        new(summary.Id, summary.Code, summary.Name, summary.CityCode, summary.CountryCode, summary.Rating);
}

public sealed record HotelSearchResponse(IReadOnlyList<HotelSummaryDto> Items);

public sealed record ProviderLookupResponse(IReadOnlyList<HotelDto> Items);

public sealed record RoomDetailDto(
    string Supplier,
    long HotelId,
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
