using OpenSearch.Client;

namespace Content.Sync.Search;

/// <summary>
/// The flattened, search-friendly shape of a hotel. <see cref="Id"/> is the source <c>_id</c> rendered as an
/// invariant string and is also the OpenSearch document id, so re-running a sync overwrites instead of duplicating.
/// </summary>
public sealed class SearchDocument
{
    public string Id { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? CanonicalId { get; set; }

    public string? Name { get; set; }

    /// <summary>Localised names; kept in the source but not indexed, because language keys are open-ended.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    public string? CityCode { get; set; }

    public string? CountryCode { get; set; }

    public double Rating { get; set; }

    public string? Address { get; set; }

    /// <summary>Sent as supplied: an out-of-range coordinate is rejected by the index and ends up dead-lettered.</summary>
    public GeoLocation? Location { get; set; }

    public List<string> Amenities { get; set; } = [];

    public List<string> Images { get; set; } = [];

    /// <summary>
    /// Flat <c>"{supplier}:{hotelId}"</c> keys. A flat keyword array answers "does this hotel list supplier X with
    /// id Y" with one term query; two conditions on a non-nested object array could match different entries.
    /// </summary>
    public List<string> ProviderKeys { get; set; } = [];

    /// <summary>Kept in the stored source so a read can be answered from the index alone; not indexed.</summary>
    public List<SearchRoom> Rooms { get; set; } = [];

    /// <summary>Kept in the stored source like <see cref="Rooms"/>; not indexed.</summary>
    public List<SearchProvider> Providers { get; set; } = [];
}

/// <summary>A room as kept in the stored source of a search document (not indexed).</summary>
public sealed class SearchRoom
{
    public string? Id { get; set; }

    public string? GroupId { get; set; }

    public string? Name { get; set; }
}

/// <summary>A supplier hotel id as kept in the stored source of a search document (not indexed; see <c>ProviderKeys</c> for the searchable form).</summary>
public sealed class SearchProvider
{
    public string? Supplier { get; set; }

    public string? HotelId { get; set; }
}
