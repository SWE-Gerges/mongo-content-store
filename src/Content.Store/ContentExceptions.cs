namespace Content.Store;

/// <summary>The supplier key is not among the configured suppliers.</summary>
public sealed class UnknownSupplierException(string supplier)
    : Exception($"Supplier '{supplier}' is not configured.")
{
    public string Supplier { get; } = supplier;
}

/// <summary>A paging cursor could not be decoded.</summary>
public sealed class InvalidCursorException()
    : Exception("The cursor is not valid.");

/// <summary>A request parameter is outside what the store accepts.</summary>
public sealed class InvalidQueryException(string message) : Exception(message);
