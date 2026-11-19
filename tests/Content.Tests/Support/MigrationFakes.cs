using System.Collections.Concurrent;
using Content.Migration;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;

namespace Content.Tests.Support;

/// <summary>Source rows in memory, paged by key like a relational source would be, with counters for what was read and when.</summary>
internal sealed class InMemoryRowSource : IRowSource
{
    private readonly List<SourceRow> _rows = [];
    private int _reads;

    public long NullKeyRows { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public int LargestPage { get; private set; }

    /// <summary>Called before each read with the 1-based read number; may throw or block.</summary>
    public Func<int, CancellationToken, Task>? BeforeRead { get; set; }

    public InMemoryRowSource Add(params SourceRow[] rows)
    {
        _rows.AddRange(rows);
        _rows.Sort((a, b) => Comparer<long>.Default.Compare((long)a.Key, (long)b.Key));
        return this;
    }

    public static SourceRow Row(long id, string? name = null, object? rating = null, string? code = null) => new(
        id,
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["code"] = code ?? $"H{id}",
            ["name"] = name ?? $"Hotel {id}",
            ["city_code"] = "C001",
            ["rating"] = rating ?? 4.5,
            ["group_id"] = id % 2 == 0 ? id : (object)$"G{id}",
        });

    public InMemoryRowSource WithRows(int count)
    {
        Add([.. Enumerable.Range(1, count).Select(i => Row(i))]);
        return this;
    }

    public async Task<RowBatch> ReadBatchAsync(MigrationDefinition definition, object? afterKey, int limit, CancellationToken cancellationToken)
    {
        var read = Interlocked.Increment(ref _reads);
        if (BeforeRead is not null)
        {
            await BeforeRead(read, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = _rows.Where(r => afterKey is null || (long)r.Key > (long)afterKey).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        var rows = hasMore ? page[..limit] : page;
        LargestPage = Math.Max(LargestPage, rows.Count);
        return new RowBatch(rows, hasMore);
    }

    public Task<long> CountAsync(MigrationDefinition definition, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Count + NullKeyRows);
}

/// <summary>Target collection in memory with the replace-by-id semantics of the Mongo upsert.</summary>
internal sealed class InMemoryMigrationTarget : IMigrationTarget
{
    private readonly ConcurrentDictionary<string, BsonDocument> _documents = new(StringComparer.Ordinal);
    private int _upsertCalls;
    private long _written;

    public int UpsertCalls => Volatile.Read(ref _upsertCalls);

    public long DocumentsWritten => Interlocked.Read(ref _written);

    public int LargestWrite { get; private set; }

    /// <summary>Given the 1-based upsert call number, returns an exception to throw instead of writing.</summary>
    public Func<int, Exception?>? FailUpsertWhen { get; set; }

    /// <summary>Documents the target reports as written but loses, as a faulty write path would.</summary>
    public Func<BsonDocument, bool>? SilentlyDropWhen { get; set; }

    public IReadOnlyCollection<string> Ids => [.. _documents.Keys];

    public BsonDocument? Get(long id) => _documents.GetValueOrDefault(id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Preload(BsonDocument document) => _documents[document["_id"].ToString()!] = document;

    public Task UpsertAsync(string collection, IReadOnlyList<BsonDocument> documents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _upsertCalls);
        if (FailUpsertWhen?.Invoke(call) is { } failure)
        {
            throw failure;
        }

        LargestWrite = Math.Max(LargestWrite, documents.Count);
        foreach (var document in documents)
        {
            Interlocked.Increment(ref _written);
            if (SilentlyDropWhen?.Invoke(document) != true)
            {
                _documents[document["_id"].ToString()!] = document;
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> CountAsync(string collection, CancellationToken cancellationToken) => Task.FromResult((long)_documents.Count);
}

/// <summary>A uniquely named shared in-memory SQLite database that lives as long as this object, so connections opened per batch all see it.</summary>
internal sealed class SqliteSource : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    private SqliteSource(string connectionString)
    {
        ConnectionString = connectionString;
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    public static SqliteSource Create() =>
        new($"Data Source=file:mig{Guid.NewGuid():N}?mode=memory&cache=shared");

    public static SqliteSource WithHotels(int count)
    {
        var source = Create();
        source.Execute("CREATE TABLE hotels (id INTEGER, code TEXT, name TEXT, city_code TEXT, rating TEXT, group_id TEXT)");
        source.InsertHotels(1, count);
        return source;
    }

    public void InsertHotels(int first, int count)
    {
        using var transaction = _keepAlive.BeginTransaction();
        using var command = _keepAlive.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO hotels (id, code, name, city_code, rating, group_id) VALUES (@id, @code, @name, 'C001', @rating, @group)";
        var id = command.Parameters.Add("@id", SqliteType.Integer);
        var code = command.Parameters.Add("@code", SqliteType.Text);
        var name = command.Parameters.Add("@name", SqliteType.Text);
        var rating = command.Parameters.Add("@rating", SqliteType.Text);
        var group = command.Parameters.Add("@group", SqliteType.Text);
        for (var i = first; i < first + count; i++)
        {
            id.Value = i;
            code.Value = $"H{i}";
            name.Value = $"Hotel {i}";
            rating.Value = i % 3 == 0 ? "4" : "4.5";
            group.Value = i % 2 == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"G{i}";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Execute(string sql)
    {
        using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public MigrationDefinition Definition(int batchSize = 100) => new()
    {
        Name = "hotels-from-sqlite",
        Provider = "sqlite",
        ConnectionString = ConnectionString,
        Query = "SELECT id, code, name, city_code, rating, group_id FROM hotels",
        KeyColumn = "id",
        KeyType = KeyKind.Integer,
        TargetCollection = "hotels",
        BatchSize = batchSize,
        Columns =
        [
            new ColumnMapping { Column = "id", Field = "_id", Type = FieldType.Integer, Required = true },
            new ColumnMapping { Column = "code", Field = "Code", Type = FieldType.String },
            new ColumnMapping { Column = "name", Field = "Name", Type = FieldType.String, Required = true },
            new ColumnMapping { Column = "city_code", Field = "CityCode", Type = FieldType.String },
            new ColumnMapping { Column = "rating", Field = "Rating", Type = FieldType.Double },
            new ColumnMapping { Column = "group_id", Field = "GroupId", Type = FieldType.StringOrNumber },
        ],
    };

    public void Dispose() => _keepAlive.Dispose();
}
