using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Content.Migration;

/// <summary>One keyset page of source rows in ascending key order.</summary>
public sealed record RowBatch(IReadOnlyList<SourceRow> Rows, bool HasMore);

/// <summary>Reads a relational source one keyset page at a time and counts it.</summary>
public interface IRowSource
{
    /// <param name="afterKey">Exclusive lower bound on the key (a <see cref="long"/> or a <see cref="string"/>), or null to start at the beginning.</param>
    /// <param name="limit">Rows in the page; one more is read internally to know whether another page follows.</param>
    Task<RowBatch> ReadBatchAsync(MigrationDefinition definition, object? afterKey, int limit, CancellationToken cancellationToken);

    /// <summary>Number of rows the migration's query returns, including rows with a null key.</summary>
    Task<long> CountAsync(MigrationDefinition definition, CancellationToken cancellationToken);
}

/// <summary>The SQL a provider needs: keyset paging and counting over the configured query, which is wrapped as a derived table.</summary>
public interface ISqlDialect
{
    /// <summary>
    /// A page query with the parameters <c>@limit</c> and, when <paramref name="hasAfter"/>, <c>@after</c>. Rows with a
    /// null key are excluded so that paging on the key cannot skip or repeat rows.
    /// </summary>
    string BuildPageQuery(string query, string keyColumn, bool hasAfter);

    string BuildCountQuery(string query);
}

/// <summary>A relational provider: its SQL dialect and how to open a connection.</summary>
public interface IDbProvider
{
    string Name { get; }

    ISqlDialect Dialect { get; }

    DbConnection CreateConnection(string connectionString);
}

public sealed class SqliteDialect : ISqlDialect
{
    public string BuildPageQuery(string query, string keyColumn, bool hasAfter) =>
        $"SELECT * FROM ({Trim(query)}) AS src WHERE src.\"{keyColumn}\" IS NOT NULL"
        + (hasAfter ? $" AND src.\"{keyColumn}\" > @after" : string.Empty)
        + $" ORDER BY src.\"{keyColumn}\" LIMIT @limit";

    public string BuildCountQuery(string query) => $"SELECT COUNT(*) FROM ({Trim(query)}) AS src";

    private static string Trim(string query) => query.Trim();
}

public sealed class SqlServerDialect : ISqlDialect
{
    public string BuildPageQuery(string query, string keyColumn, bool hasAfter) =>
        $"SELECT TOP (@limit) * FROM ({Trim(query)}) AS src WHERE src.[{keyColumn}] IS NOT NULL"
        + (hasAfter ? $" AND src.[{keyColumn}] > @after" : string.Empty)
        + $" ORDER BY src.[{keyColumn}]";

    public string BuildCountQuery(string query) => $"SELECT COUNT(*) FROM ({Trim(query)}) AS src";

    private static string Trim(string query) => query.Trim();
}

internal sealed class SqliteProvider : IDbProvider
{
    public string Name => "sqlite";

    public ISqlDialect Dialect { get; } = new SqliteDialect();

    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);
}

internal sealed class SqlServerProvider : IDbProvider
{
    public string Name => "sqlserver";

    public ISqlDialect Dialect { get; } = new SqlServerDialect();

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);
}

/// <summary>
/// <see cref="IRowSource"/> over any ADO.NET provider. Each page is one query with a <c>LIMIT</c>/<c>TOP</c> of the page
/// size plus one, read through a <see cref="DbDataReader"/> row by row, so memory is bounded by the page and nothing of
/// the source is ever loaded as a whole. Connections are opened per page and disposed with it.
/// </summary>
internal sealed class DbRowSource(IEnumerable<IDbProvider> providers) : IRowSource
{
    private readonly Dictionary<string, IDbProvider> _providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public async Task<RowBatch> ReadBatchAsync(MigrationDefinition definition, object? afterKey, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var provider = Resolve(definition);

        await using var connection = provider.CreateConnection(definition.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = provider.Dialect.BuildPageQuery(definition.Query, definition.KeyColumn, afterKey is not null);
        if (afterKey is not null)
        {
            AddParameter(command, "@after", afterKey);
        }

        AddParameter(command, "@limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinals = definition.Columns.Select(c => (c.Column, Ordinal: Ordinal(reader, definition, c.Column))).ToList();
        var keyOrdinal = Ordinal(reader, definition, definition.KeyColumn);

        var rows = new List<SourceRow>(Math.Min(limit + 1, 4096));
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(ordinals.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (column, ordinal) in ordinals)
            {
                values[column] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            rows.Add(new SourceRow(ReadKey(reader.GetValue(keyOrdinal), definition), values));
            if (rows.Count > limit)
            {
                break;
            }
        }

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new RowBatch(rows, hasMore);
    }

    public async Task<long> CountAsync(MigrationDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var provider = Resolve(definition);

        await using var connection = provider.CreateConnection(definition.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = provider.Dialect.BuildCountQuery(definition.Query);
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private IDbProvider Resolve(MigrationDefinition definition) =>
        _providers.TryGetValue(definition.Provider, out var provider)
            ? provider
            : throw new InvalidOperationException($"Migration '{definition.Name}' uses the unknown provider '{definition.Provider}'.");

    private static int Ordinal(DbDataReader reader, MigrationDefinition definition, string column)
    {
        try
        {
            return reader.GetOrdinal(column);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"The query of migration '{definition.Name}' returns no column '{column}'.");
        }
    }

    private static object ReadKey(object value, MigrationDefinition definition) => definition.KeyType == KeyKind.Integer
        ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
