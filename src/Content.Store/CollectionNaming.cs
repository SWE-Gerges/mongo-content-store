using Content.Store.Configuration;

namespace Content.Store;

/// <summary>Maps supplier keys to collection names. A new supplier is configuration, not code.</summary>
public interface ICollectionNaming
{
    /// <summary>The merged master collection.</summary>
    string Master { get; }

    string ForSupplier(string supplierKey);
}

public sealed class HotelCollectionNaming : ICollectionNaming
{
    public string Master => "hotels";

    public string ForSupplier(string supplierKey)
    {
        if (!SupplierKey.IsValid(supplierKey))
        {
            throw new ArgumentException($"'{supplierKey}' is not a valid supplier key.", nameof(supplierKey));
        }

        return $"hotel_{supplierKey}";
    }
}
