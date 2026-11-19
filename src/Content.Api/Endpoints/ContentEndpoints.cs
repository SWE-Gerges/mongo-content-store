using System.Globalization;
using Content.Api.Contracts;
using Content.Reads;
using Content.Store;
using Microsoft.AspNetCore.Mvc;

namespace Content.Api.Endpoints;

internal static class ContentEndpoints
{
    public const string SourceHeader = "X-Content-Source";

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/content").WithTags("Content");

        group.MapGet("/{supplier}/hotels", GetHotels)
            .WithName("GetHotels")
            .WithSummary("Looks hotels up by id, or pages the hotel ids of a city.")
            .WithDescription(
                "Pass 'ids' (comma separated, numeric) to fetch hotels by id; or pass 'cityCode' with optional 'pageSize' and "
                + "'cursor' to page the ids of a city. The two modes are mutually exclusive. Lookups by id are answered by the "
                + "search index first and completed from MongoDB; the X-Content-Source response header (index, mongo or mixed) says which "
                + "served the documents. City paging always reads MongoDB.")
            .Produces<HotelCollectionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{supplier}/hotels/by-code/{code}", GetHotelByCode)
            .WithName("GetHotelByCode")
            .WithSummary("Fetches a hotel by the supplier's alphanumeric code.")
            .WithDescription("Answered by the search index first and completed from MongoDB; see the X-Content-Source response header.")
            .Produces<HotelDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/hotels/search", SearchHotels)
            .WithName("SearchHotels")
            .WithSummary("Searches the merged hotel master by name.")
            .WithDescription(
                "'anchored' (default) matches a case-sensitive prefix and can use an index. 'contains' is a case-insensitive "
                + "substring match that scans the collection, so use it only as a fallback. 'ci' is a case-insensitive "
                + "whole-name match served by a collation index.")
            .Produces<HotelSearchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/hotels/by-provider/{supplier}/{hotelId}", FindByProvider)
            .WithName("FindHotelsByProvider")
            .WithSummary("Finds merged hotels that list a supplier's hotel id.")
            .Produces<ProviderLookupResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/{supplier}/hotels/{id:long}/rooms/{roomId}/detail", GetRoomDetail)
            .WithName("GetRoomDetail")
            .WithSummary("Returns a room's supplier-neutral detail, produced by the supplier's room mapper.")
            .Produces<RoomDetailDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<IResult> GetHotels(
        string supplier,
        [FromQuery] string? ids,
        [FromQuery] string? cityCode,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        IHotelContentStore store,
        IContentReader reader,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var byIds = !string.IsNullOrWhiteSpace(ids);
        var byCity = !string.IsNullOrWhiteSpace(cityCode);
        if (byIds == byCity)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "Pass either 'ids' or 'cityCode'.");
        }

        if (byCity)
        {
            var page = await store.GetByCityAsync(supplier, cityCode!, pageSize, cursor, cancellationToken);
            SetSource(http, ContentSource.Mongo);
            return TypedResults.Ok(new HotelCollectionResponse(null, page.Ids, page.NextCursor));
        }

        var parsed = new List<long>();
        foreach (var token in ids!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid request", $"'{token}' is not a valid numeric hotel id.");
            }

            parsed.Add(id);
        }

        var result = await reader.GetByIdsAsync(supplier, parsed, cancellationToken);
        SetSource(http, result.Source);
        return TypedResults.Ok(new HotelCollectionResponse(result.Items.OrderBy(h => h.Id).Select(HotelDto.From).ToList(), null, null));
    }

    private static async Task<IResult> GetHotelByCode(string supplier, string code, IContentReader reader, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await reader.GetByCodesAsync(supplier, [code], cancellationToken);
        if (result.Items.Count == 0)
        {
            return Problem(StatusCodes.Status404NotFound, "Hotel not found", $"Supplier '{supplier}' has no hotel with code '{code}'.");
        }

        SetSource(http, result.Source);
        return TypedResults.Ok(HotelDto.From(result.Items[0]));
    }

    /// <summary>Tells the caller whether the answer came from the search index, MongoDB, or both.</summary>
    private static void SetSource(HttpContext http, ContentSource source) =>
        http.Response.Headers[SourceHeader] = source.ToHeaderValue();

    private static async Task<IResult> SearchHotels(
        [FromQuery] string? name,
        [FromQuery] string? countryCode,
        [FromQuery] string? mode,
        [FromQuery] int? limit,
        IHotelContentStore store,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!TryParseMode(mode, out var searchMode))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "'mode' must be one of: anchored, contains, ci.");
        }

        var results = await store.SearchByNameAsync(name ?? string.Empty, countryCode, searchMode, limit, cancellationToken);
        SetSource(http, ContentSource.Mongo);
        return TypedResults.Ok(new HotelSearchResponse(results.Select(HotelSummaryDto.From).ToList()));
    }

    private static async Task<IResult> FindByProvider(string supplier, string hotelId, IHotelContentStore store, HttpContext http, CancellationToken cancellationToken)
    {
        var hotels = await store.FindByProviderAsync(supplier, hotelId, cancellationToken);
        SetSource(http, ContentSource.Mongo);
        return TypedResults.Ok(new ProviderLookupResponse(hotels.Select(HotelDto.From).ToList()));
    }

    private static async Task<IResult> GetRoomDetail(string supplier, long id, string roomId, IHotelContentStore store, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await store.GetRoomDetailAsync(supplier, id, roomId, cancellationToken);
        SetSource(http, ContentSource.Mongo);
        return result.Status switch
        {
            RoomDetailStatus.Found => TypedResults.Ok(new RoomDetailDto(
                supplier,
                id,
                result.Detail!.RoomId,
                result.Detail.Name,
                result.Detail.GroupId,
                result.Detail.Occupancy,
                result.Detail.RoomType,
                result.Detail.RoomClass,
                result.Detail.Quality,
                result.Detail.Bedding,
                result.Detail.Bathroom,
                result.Detail.CharacteristicCodes)),
            RoomDetailStatus.HotelNotFound => Problem(StatusCodes.Status404NotFound, "Hotel not found", $"Supplier '{supplier}' has no hotel {id}."),
            RoomDetailStatus.RoomNotFound => Problem(StatusCodes.Status404NotFound, "Room not found", $"Hotel {id} has no room '{roomId}'."),
            _ => Problem(StatusCodes.Status422UnprocessableEntity, "No room mapper", $"There is no room mapper for supplier '{supplier}'."),
        };
    }

    private static bool TryParseMode(string? mode, out NameSearchMode result)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case null or "" or "anchored":
                result = NameSearchMode.Anchored;
                return true;
            case "contains":
                result = NameSearchMode.Contains;
                return true;
            case "ci":
                result = NameSearchMode.CaseInsensitive;
                return true;
            default:
                result = default;
                return false;
        }
    }

    internal static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
