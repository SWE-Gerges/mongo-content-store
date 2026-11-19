using Content.Store;
using Microsoft.Extensions.Options;

namespace Content.Reads;

/// <summary>
/// Supplier to collection and index alias. Built from validated options (the host refuses to start on a table that
/// misses a configured supplier, repeats one or points at something the store does not read), and answers an
/// unknown supplier with <see cref="UnknownSupplierException"/>, never with a default route.
/// </summary>
public sealed class ReadRouteTable
{
    private readonly Dictionary<string, ReadRoute> _routes;

    public ReadRouteTable(IOptions<ReadsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _routes = options.Value.Routes.ToDictionary(r => r.Supplier, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ReadRoute> Routes => _routes.Values;

    public bool TryGet(string supplier, out ReadRoute route)
    {
        if (!string.IsNullOrEmpty(supplier) && _routes.TryGetValue(supplier, out var found))
        {
            route = found;
            return true;
        }

        route = null!;
        return false;
    }

    public ReadRoute Get(string supplier) =>
        TryGet(supplier, out var route) ? route : throw new UnknownSupplierException(supplier ?? string.Empty);
}
