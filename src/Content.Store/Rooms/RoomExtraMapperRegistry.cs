using System.Diagnostics.CodeAnalysis;
using Content.Store.Models;

namespace Content.Store.Rooms;

/// <summary>Outcome of asking the registry to map a room. An unknown supplier is a value, not an exception.</summary>
public readonly record struct RoomMappingResult(bool HasMapper, RoomDetail? Detail)
{
    public static RoomMappingResult NoMapper => default;

    public static RoomMappingResult Mapped(RoomDetail detail) => new(true, detail);
}

/// <summary>
/// Single point that resolves a supplier key to its <see cref="IRoomExtraMapper"/>. Two mappers claiming the same
/// supplier is a configuration error and fails at construction rather than picking one silently.
/// </summary>
public sealed class RoomExtraMapperRegistry
{
    private readonly Dictionary<string, IRoomExtraMapper> _bySupplier = new(StringComparer.Ordinal);

    public RoomExtraMapperRegistry(IEnumerable<IRoomExtraMapper> mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);
        foreach (var mapper in mappers)
        {
            foreach (var key in mapper.SupplierKeys)
            {
                if (!_bySupplier.TryAdd(key, mapper))
                {
                    throw new InvalidOperationException(
                        $"Supplier '{key}' is claimed by both {_bySupplier[key].GetType().Name} and {mapper.GetType().Name}.");
                }
            }
        }
    }

    public IReadOnlyCollection<string> SupportedSuppliers => _bySupplier.Keys;

    public bool TryGetMapper(string supplier, [NotNullWhen(true)] out IRoomExtraMapper? mapper) =>
        _bySupplier.TryGetValue(supplier, out mapper);

    public RoomMappingResult Map(string supplier, RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return TryGetMapper(supplier, out var mapper)
            ? RoomMappingResult.Mapped(mapper.Map(room))
            : RoomMappingResult.NoMapper;
    }
}
