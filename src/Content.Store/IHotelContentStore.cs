using Content.Store.Models;

namespace Content.Store;

/// <summary>How a name search matches.</summary>
public enum NameSearchMode
{
    /// <summary>Case-sensitive prefix match (<c>^name</c>); the only regex form that can use index bounds.</summary>
    Anchored,

    /// <summary>Case-insensitive substring match. Always scans: use it when an anchored search found nothing.</summary>
    Contains,

    /// <summary>Case-insensitive whole-name match answered by the collation index (<c>en</c>, strength 2).</summary>
    CaseInsensitive,
}

/// <summary>One page of hotel ids for a city. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record CityPage(IReadOnlyList<long> Ids, string? NextCursor);

public enum RoomDetailStatus
{
    Found,
    HotelNotFound,
    RoomNotFound,
    NoMapper,
}

public sealed record RoomDetailResult(RoomDetailStatus Status, RoomDetail? Detail);

/// <summary>Fast reads over hotel content. The supplier is a configured key such as <c>supplier-a</c>.</summary>
public interface IHotelContentStore
{
    /// <summary>Fetches hotels by their numeric id with a single indexed <c>$in</c> on <c>_id</c>.</summary>
    Task<IReadOnlyList<HotelDocument>> GetByIdsAsync(string supplier, IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);

    /// <summary>Fetches hotels by supplier code, for suppliers that key hotels by an alphanumeric code.</summary>
    Task<IReadOnlyList<HotelDocument>> GetByCodesAsync(string supplier, IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default);

    /// <summary>Pages hotel ids in a city with keyset paging; only ids are projected and <c>limit + 1</c> rows are read.</summary>
    Task<CityPage> GetByCityAsync(string supplier, string cityCode, int? pageSize, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>Searches the merged master collection by hotel name.</summary>
    Task<IReadOnlyList<HotelSummary>> SearchByNameAsync(
        string name,
        string? countryCode,
        NameSearchMode mode,
        int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>Finds merged hotels that list the supplier's id, whether stored as a string or a number.</summary>
    Task<IReadOnlyList<HotelDocument>> FindByProviderAsync(string supplier, string providerHotelId, CancellationToken cancellationToken = default);

    Task<RoomDetailResult> GetRoomDetailAsync(string supplier, long hotelId, string roomId, CancellationToken cancellationToken = default);
}
