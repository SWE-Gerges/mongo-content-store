using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Content.Migration;

/// <summary>How a source value is converted before it is stored.</summary>
public enum FieldType
{
    /// <summary>Any scalar, stored as text (numbers use the invariant culture).</summary>
    String,

    /// <summary>A whole number; text such as "12" and a number such as 12.0 are accepted.</summary>
    Integer,

    /// <summary>A finite floating-point number; text such as "4.5" and an integer are accepted.</summary>
    Double,

    /// <summary><c>true</c>/<c>false</c>, 1/0 or the text forms of those.</summary>
    Boolean,

    /// <summary>A point in time, stored as UTC; unspecified times are taken to be UTC.</summary>
    DateTime,

    /// <summary>A value a supplier sends as a number in one row and as text in the next; stored as text either way.</summary>
    StringOrNumber,
}

/// <summary>The type of the key column that orders and pages the source.</summary>
public enum KeyKind
{
    Integer,
    String,
}

/// <summary>Maps one source column to one document field.</summary>
public sealed class ColumnMapping
{
    public string Column { get; set; } = string.Empty;

    /// <summary>Document field; <c>_id</c> for the key.</summary>
    public string Field { get; set; } = string.Empty;

    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>A missing or empty value makes the row invalid (dead-lettered) instead of leaving the field out.</summary>
    public bool Required { get; set; }
}

/// <summary>
/// One configured migration: which relational source and query to read, which column orders it, how columns become
/// document fields, and which collection receives them. Definitions come from configuration, never from a request, so
/// the SQL text is operator-supplied.
/// </summary>
public sealed class MigrationDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary><c>sqlite</c> or <c>sqlserver</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>A SELECT without ORDER BY. It is wrapped as a derived table and paged on <see cref="KeyColumn"/>.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Unique, non-null column the query is ordered and paged by. Rows with a null key are never read (the count check reports them).</summary>
    public string KeyColumn { get; set; } = string.Empty;

    public KeyKind KeyType { get; set; } = KeyKind.Integer;

    public string TargetCollection { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 1000;

    public List<ColumnMapping> Columns { get; set; } = [];

    /// <summary>The checkpoint, lease and dead-letter key of this migration.</summary>
    public string Target => MigrationTargets.For(Name);
}

public static class MigrationTargets
{
    public const string Prefix = "migration:";

    public static string For(string name) => Prefix + name;
}

public sealed class MigrationOptions
{
    public const string SectionName = "Migration";

    public List<MigrationDefinition> Definitions { get; set; } = [];

    public MigrationDefinition? Find(string name) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
}

internal sealed partial class MigrationOptionsValidator : IValidateOptions<MigrationOptions>
{
    private static readonly string[] Providers = ["sqlite", "sqlserver"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public ValidateOptionsResult Validate(string? name, MigrationOptions options)
    {
        var failures = new List<string>();

        foreach (var duplicate in options.Definitions.GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            failures.Add($"Migration:Definitions lists '{duplicate.Key}' {duplicate.Count()} times.");
        }

        foreach (var definition in options.Definitions)
        {
            Check(definition, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(MigrationDefinition definition, List<string> failures)
    {
        var label = $"Migration '{definition.Name}'";
        if (!NamePattern().IsMatch(definition.Name) || definition.Name.Length > 60)
        {
            failures.Add($"{label}: the name must be lowercase letters, digits and single hyphens, at most 60 characters.");
        }

        if (!Providers.Contains(definition.Provider, StringComparer.Ordinal))
        {
            failures.Add($"{label}: Provider must be one of {string.Join(", ", Providers)}.");
        }

        if (string.IsNullOrWhiteSpace(definition.ConnectionString))
        {
            failures.Add($"{label}: ConnectionString is required.");
        }

        if (string.IsNullOrWhiteSpace(definition.Query))
        {
            failures.Add($"{label}: Query is required.");
        }
        else if (definition.Query.Contains(';', StringComparison.Ordinal))
        {
            failures.Add($"{label}: Query must be a single SELECT statement (no ';').");
        }

        if (!IdentifierPattern().IsMatch(definition.KeyColumn))
        {
            failures.Add($"{label}: KeyColumn must be a plain column name (letters, digits and underscores).");
        }

        if (definition.BatchSize is < 1 or > 100_000)
        {
            failures.Add($"{label}: BatchSize must be between 1 and 100000.");
        }

        var collection = definition.TargetCollection;
        if (string.IsNullOrWhiteSpace(collection)
            || collection.Contains('$', StringComparison.Ordinal)
            || collection.Contains('\0', StringComparison.Ordinal)
            || collection.StartsWith("system.", StringComparison.Ordinal)
            || collection.StartsWith("_sync_", StringComparison.Ordinal)
            || collection.StartsWith("sync_", StringComparison.Ordinal)
            || collection == "index_versions")
        {
            failures.Add($"{label}: TargetCollection must be a collection name that is not reserved for the service's own bookkeeping.");
        }

        if (definition.Columns.Count == 0)
        {
            failures.Add($"{label}: Columns must map at least the key column.");
            return;
        }

        foreach (var column in definition.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Column))
            {
                failures.Add($"{label}: every column mapping needs a Column.");
            }

            if (string.IsNullOrWhiteSpace(column.Field)
                || column.Field.Contains('$', StringComparison.Ordinal)
                || column.Field.Contains('.', StringComparison.Ordinal)
                || column.Field.Contains('\0', StringComparison.Ordinal))
            {
                failures.Add($"{label}: field '{column.Field}' is not a valid document field name.");
            }
        }

        foreach (var duplicate in definition.Columns.GroupBy(c => c.Field, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            failures.Add($"{label}: field '{duplicate.Key}' is mapped {duplicate.Count()} times.");
        }

        var key = definition.Columns.FirstOrDefault(c => string.Equals(c.Column, definition.KeyColumn, StringComparison.OrdinalIgnoreCase));
        if (key is null || key.Field != "_id")
        {
            failures.Add($"{label}: the key column '{definition.KeyColumn}' must be mapped to the field '_id'.");
        }
        else
        {
            var expected = definition.KeyType == KeyKind.Integer ? FieldType.Integer : FieldType.String;
            if (key.Type != expected)
            {
                failures.Add($"{label}: the key column is declared as {definition.KeyType}, so it must be mapped with Type {expected}.");
            }
        }

        if (definition.Columns.Count(c => c.Field == "_id") > 1)
        {
            failures.Add($"{label}: only the key column may map to '_id'.");
        }
    }
}
