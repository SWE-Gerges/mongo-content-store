using OpenSearch.Client;

namespace Content.Sync.Search;

/// <summary>
/// Explicit index mapping. It is strict, so a field that is not declared here is rejected instead of silently
/// guessed, and identifiers are keywords so they are matched exactly.
/// </summary>
internal static class HotelIndexMapping
{
    public static ITypeMapping Describe(TypeMappingDescriptor<SearchDocument> map) => map
        .Dynamic(DynamicMapping.Strict)
        .Properties(properties => properties
            .Keyword(k => k.Name(d => d.Id))
            .Keyword(k => k.Name(d => d.Code))
            .Keyword(k => k.Name(d => d.CanonicalId))
            .Text(t => t
                .Name(d => d.Name)
                .Fields(f => f.Keyword(k => k.Name("keyword").IgnoreAbove(256))))
            .Object<object>(o => o.Name(d => d.Names).Enabled(false))
            .Keyword(k => k.Name(d => d.CityCode))
            .Keyword(k => k.Name(d => d.CountryCode))
            .Number(n => n.Name(d => d.Rating).Type(NumberType.Double))
            .Text(t => t.Name(d => d.Address))
            .GeoPoint(g => g.Name(d => d.Location))
            .Keyword(k => k.Name(d => d.Amenities))
            .Keyword(k => k.Name(d => d.Images).Index(false))
            .Keyword(k => k.Name(d => d.ProviderKeys))
            .Object<object>(o => o.Name(d => d.Rooms).Enabled(false))
            .Object<object>(o => o.Name(d => d.Providers).Enabled(false)));
}
