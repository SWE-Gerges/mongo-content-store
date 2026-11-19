using System.Data.Common;
using Content.Migration;
using Content.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Content.Tests.Migration;

public class RowMapperTests
{
    private static MigrationDefinition Definition(FieldType type, bool required = false) => new()
    {
        Name = "m",
        KeyColumn = "id",
        Columns =
        [
            new ColumnMapping { Column = "id", Field = "_id", Type = FieldType.Integer, Required = true },
            new ColumnMapping { Column = "value", Field = "Value", Type = type, Required = required },
        ],
    };

    private static MappedRow Map(FieldType type, object? value, bool required = false) =>
        RowMapper.Map(Definition(type, required), new SourceRow(1L, new Dictionary<string, object?> { ["id"] = 1L, ["value"] = value }));

    [Theory]
    [InlineData(12L, 12L)]
    [InlineData(12, 12L)]
    [InlineData("12", 12L)]
    [InlineData(" 12 ", 12L)]
    [InlineData(12.0, 12L)]
    [InlineData("12.0", 12L)]
    public void Integers_are_read_from_numbers_and_text(object raw, long expected)
    {
        var mapped = Map(FieldType.Integer, raw);

        mapped.IsValid.ShouldBeTrue();
        mapped.Document!["Value"].ShouldBe(new BsonInt64(expected));
    }

    [Theory]
    [InlineData(12.5)]
    [InlineData("12.5")]
    [InlineData("twelve")]
    [InlineData(true)]
    public void Integers_that_are_not_whole_or_not_numbers_make_the_row_invalid(object raw)
    {
        var mapped = Map(FieldType.Integer, raw);

        mapped.IsValid.ShouldBeFalse();
        mapped.Problems.ShouldHaveSingleItem().ShouldContain("cannot be read as Integer");
    }

    [Theory]
    [InlineData(4.5, 4.5)]
    [InlineData(4L, 4.0)]
    [InlineData("4.5", 4.5)]
    [InlineData("4", 4.0)]
    public void Doubles_are_read_from_numbers_and_text_with_the_invariant_culture(object raw, double expected) =>
        Map(FieldType.Double, raw).Document!["Value"].ShouldBe(new BsonDouble(expected));

    [Theory]
    [InlineData("4,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("n/a")]
    public void Doubles_that_are_not_finite_numbers_make_the_row_invalid(string raw) =>
        Map(FieldType.Double, raw).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData(true, true)]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    public void Booleans_accept_flags_bits_and_their_text_forms(object raw, bool expected) =>
        Map(FieldType.Boolean, raw).Document!["Value"].ShouldBe(new BsonBoolean(expected));

    [Theory]
    [InlineData(2L)]
    [InlineData("maybe")]
    public void Booleans_reject_anything_else(object raw) => Map(FieldType.Boolean, raw).IsValid.ShouldBeFalse();

    [Fact]
    public void Dates_become_utc_whether_they_arrive_as_values_or_text()
    {
        Map(FieldType.DateTime, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Unspecified)).Document!["Value"].AsBsonDateTime.ToUniversalTime().ShouldBe(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        Map(FieldType.DateTime, "2026-03-01T10:00:00+02:00").Document!["Value"].AsBsonDateTime.ToUniversalTime().ShouldBe(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc));
        Map(FieldType.DateTime, "yesterday").IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(17L, "17")]
    [InlineData("17", "17")]
    [InlineData(4.5, "4.5")]
    [InlineData("G-17", "G-17")]
    public void A_value_that_is_a_number_in_one_row_and_text_in_the_next_is_stored_as_text(object raw, string expected) =>
        Map(FieldType.StringOrNumber, raw).Document!["Value"].ShouldBe(new BsonString(expected));

    [Fact]
    public void Strings_keep_their_text_and_binary_values_are_refused()
    {
        Map(FieldType.String, "Grand Example").Document!["Value"].ShouldBe(new BsonString("Grand Example"));
        Map(FieldType.String, new byte[] { 1, 2 }).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_optional_value_leaves_the_field_out_and_a_missing_required_one_invalidates_the_row(string? raw)
    {
        var optional = Map(FieldType.String, raw);
        var required = Map(FieldType.String, raw, required: true);

        optional.IsValid.ShouldBeTrue();
        optional.Document!.Contains("Value").ShouldBeFalse();
        required.IsValid.ShouldBeFalse();
        required.Problems.ShouldHaveSingleItem().ShouldContain("column 'value' is required but empty");
    }

    [Fact]
    public void A_sql_null_is_missing_too() =>
        Map(FieldType.Integer, DBNull.Value, required: true).IsValid.ShouldBeFalse();

    [Fact]
    public void Every_problem_of_a_row_is_reported_together()
    {
        var definition = new MigrationDefinition
        {
            Name = "m",
            KeyColumn = "id",
            Columns =
            [
                new ColumnMapping { Column = "id", Field = "_id", Type = FieldType.Integer, Required = true },
                new ColumnMapping { Column = "a", Field = "A", Type = FieldType.Integer },
                new ColumnMapping { Column = "b", Field = "B", Type = FieldType.Double },
                new ColumnMapping { Column = "c", Field = "C", Required = true },
            ],
        };
        var row = new SourceRow(1L, new Dictionary<string, object?> { ["id"] = 1L, ["a"] = "x", ["b"] = "y", ["c"] = null });

        var mapped = RowMapper.Map(definition, row);

        mapped.Problems.Count.ShouldBe(3);
    }

    [Fact]
    public void The_payload_hash_follows_the_row_values()
    {
        var one = Map(FieldType.String, "a");
        var same = Map(FieldType.String, "a");
        var other = Map(FieldType.String, "b");

        one.PayloadHash.ShouldBe(same.PayloadHash);
        one.PayloadHash.ShouldNotBe(other.PayloadHash);
        one.PayloadHash.Length.ShouldBe(64);
    }
}

public class DbRowSourceTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly List<SqliteSource> _sources = [];

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    private SqliteSource Hotels(int count)
    {
        var source = SqliteSource.WithHotels(count);
        _sources.Add(source);
        return source;
    }

    private static DbRowSource RowSource() => new(Providers());

    private static IDbProvider[] Providers() => [new SqliteProvider(), new SqlServerProvider()];

    [Fact]
    public async Task Pages_come_back_in_key_order_and_say_whether_more_follow()
    {
        var source = Hotels(25);
        var definition = source.Definition();
        var rows = RowSource();

        var first = await rows.ReadBatchAsync(definition, null, 10, _ct);
        var second = await rows.ReadBatchAsync(definition, first.Rows[^1].Key, 10, _ct);
        var third = await rows.ReadBatchAsync(definition, second.Rows[^1].Key, 10, _ct);

        first.Rows.Select(r => r.Key).ShouldBe(Enumerable.Range(1, 10).Select(i => (object)(long)i));
        first.HasMore.ShouldBeTrue();
        second.Rows.Select(r => (long)r.Key).ShouldBe(Enumerable.Range(11, 10).Select(i => (long)i));
        second.HasMore.ShouldBeTrue();
        third.Rows.Select(r => (long)r.Key).ShouldBe(Enumerable.Range(21, 5).Select(i => (long)i));
        third.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task A_page_that_ends_exactly_on_the_last_row_has_nothing_more()
    {
        var source = Hotels(20);

        var page = await RowSource().ReadBatchAsync(source.Definition(), 10L, 10, _ct);

        page.Rows.Count.ShouldBe(10);
        page.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Rows_carry_the_values_of_the_mapped_columns()
    {
        var source = Hotels(3);

        var page = await RowSource().ReadBatchAsync(source.Definition(), null, 10, _ct);

        var row = page.Rows[1];
        row.Values["name"].ShouldBe("Hotel 2");
        row.Values["rating"].ShouldBe("4.5");
        row.Values["group_id"].ShouldBe("2");
        row.Values["CODE"].ShouldBe("H2", "column names are matched without regard to case");
    }

    [Fact]
    public async Task Walking_every_page_of_a_large_source_returns_each_row_once_and_never_more_than_a_page()
    {
        var source = Hotels(5000);
        var definition = source.Definition();
        var rows = RowSource();
        var keys = new List<long>();
        var largest = 0;
        var pages = 0;
        object? after = null;

        while (true)
        {
            var page = await rows.ReadBatchAsync(definition, after, 100, _ct);
            largest = Math.Max(largest, page.Rows.Count);
            pages++;
            keys.AddRange(page.Rows.Select(r => (long)r.Key));
            if (!page.HasMore)
            {
                break;
            }

            after = page.Rows[^1].Key;
        }

        keys.ShouldBe(Enumerable.Range(1, 5000).Select(i => (long)i));
        largest.ShouldBe(100);
        pages.ShouldBe(50);
    }

    [Fact]
    public async Task Rows_with_a_null_key_are_not_paged_but_are_counted()
    {
        var source = Hotels(10);
        source.Execute("INSERT INTO hotels (id, code, name) VALUES (NULL, 'X', 'No key')");
        var definition = source.Definition();

        var page = await RowSource().ReadBatchAsync(definition, null, 100, _ct);
        var count = await RowSource().CountAsync(definition, _ct);

        page.Rows.Count.ShouldBe(10);
        count.ShouldBe(11, "the count includes the row that can never be paged");
    }

    [Fact]
    public async Task String_keys_page_in_text_order()
    {
        var source = SqliteSource.Create();
        _sources.Add(source);
        source.Execute("CREATE TABLE hotels (code TEXT, name TEXT)");
        foreach (var code in new[] { "B2", "A1", "C3", "A2" })
        {
            source.Execute($"INSERT INTO hotels VALUES ('{code}', 'Hotel {code}')");
        }

        var definition = new MigrationDefinition
        {
            Name = "by-code",
            Provider = "sqlite",
            ConnectionString = source.ConnectionString,
            Query = "SELECT code, name FROM hotels",
            KeyColumn = "code",
            KeyType = KeyKind.String,
            TargetCollection = "hotels",
            Columns = [new ColumnMapping { Column = "code", Field = "_id", Type = FieldType.String, Required = true }, new ColumnMapping { Column = "name", Field = "Name" }],
        };

        var first = await RowSource().ReadBatchAsync(definition, null, 2, _ct);
        var second = await RowSource().ReadBatchAsync(definition, first.Rows[^1].Key, 2, _ct);

        first.Rows.Select(r => (string)r.Key).ShouldBe(["A1", "A2"]);
        second.Rows.Select(r => (string)r.Key).ShouldBe(["B2", "C3"]);
        second.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task A_query_that_lacks_a_mapped_column_fails_with_a_clear_message()
    {
        var source = Hotels(3);
        var definition = source.Definition();
        definition.Columns.Add(new ColumnMapping { Column = "missing_column", Field = "X" });

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => RowSource().ReadBatchAsync(definition, null, 10, _ct));

        failure.Message.ShouldContain("returns no column 'missing_column'");
    }

    [Fact]
    public async Task An_unknown_provider_is_refused()
    {
        var definition = Hotels(1).Definition();
        definition.Provider = "oracle";

        await Should.ThrowAsync<InvalidOperationException>(() => RowSource().ReadBatchAsync(definition, null, 10, _ct));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_read()
    {
        var source = Hotels(10);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => RowSource().ReadBatchAsync(source.Definition(), null, 10, cancelled.Token));
    }

    [Fact]
    public void The_sqlite_dialect_pages_with_limit_and_excludes_null_keys()
    {
        var sql = new SqliteDialect().BuildPageQuery("SELECT id, name FROM hotels", "id", hasAfter: true);

        sql.ShouldBe("SELECT * FROM (SELECT id, name FROM hotels) AS src WHERE src.\"id\" IS NOT NULL AND src.\"id\" > @after ORDER BY src.\"id\" LIMIT @limit");
        new SqliteDialect().BuildPageQuery("SELECT id FROM hotels", "id", hasAfter: false).ShouldNotContain("@after");
        new SqliteDialect().BuildCountQuery(" SELECT id FROM hotels ").ShouldBe("SELECT COUNT(*) FROM (SELECT id FROM hotels) AS src");
    }

    [Fact]
    public void The_sql_server_dialect_pages_with_top_and_excludes_null_keys()
    {
        var sql = new SqlServerDialect().BuildPageQuery("SELECT id, name FROM dbo.Hotels", "id", hasAfter: true);

        sql.ShouldBe("SELECT TOP (@limit) * FROM (SELECT id, name FROM dbo.Hotels) AS src WHERE src.[id] IS NOT NULL AND src.[id] > @after ORDER BY src.[id]");
        new SqlServerDialect().BuildPageQuery("SELECT id FROM dbo.Hotels", "id", hasAfter: false).ShouldNotContain("@after");
        new SqlServerDialect().BuildCountQuery("SELECT id FROM dbo.Hotels").ShouldBe("SELECT COUNT(*) FROM (SELECT id FROM dbo.Hotels) AS src");
    }

    [Fact]
    public void The_sql_server_provider_builds_a_sql_client_connection_without_touching_a_server()
    {
        var provider = Providers().Single(p => p.Name == "sqlserver");

        using DbConnection connection = provider.CreateConnection("Server=sql.example.com;Database=catalog;Integrated Security=true;TrustServerCertificate=true");

        connection.ShouldBeOfType<SqlConnection>();
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }
}

public class MigrationOptionsTests
{
    private static MigrationDefinition Valid() => new()
    {
        Name = "hotels-from-sqlite",
        Provider = "sqlite",
        ConnectionString = "Data Source=demo.db",
        Query = "SELECT id, name FROM hotels",
        KeyColumn = "id",
        TargetCollection = "hotels_imported",
        BatchSize = 500,
        Columns =
        [
            new ColumnMapping { Column = "id", Field = "_id", Type = FieldType.Integer, Required = true },
            new ColumnMapping { Column = "name", Field = "Name" },
        ],
    };

    private static ValidateOptionsResult Validate(params MigrationDefinition[] definitions) =>
        new MigrationOptionsValidator().Validate(null, new MigrationOptions { Definitions = [.. definitions] });

    [Fact]
    public void A_sound_definition_passes() => Validate(Valid()).Succeeded.ShouldBeTrue();

    [Fact]
    public void No_definitions_at_all_is_fine() => Validate().Succeeded.ShouldBeTrue();

    public static TheoryData<string, Action<MigrationDefinition>> Breakages => new()
    {
        { "name", d => d.Name = "Not Valid" },
        { "Provider", d => d.Provider = "oracle" },
        { "ConnectionString", d => d.ConnectionString = " " },
        { "Query is required", d => d.Query = "" },
        { "single SELECT", d => d.Query = "SELECT 1; DROP TABLE hotels" },
        { "KeyColumn", d => d.KeyColumn = "id; --" },
        { "BatchSize", d => d.BatchSize = 0 },
        { "TargetCollection", d => d.TargetCollection = "_sync_checkpoints" },
        { "TargetCollection", d => d.TargetCollection = "sync_runs" },
        { "TargetCollection", d => d.TargetCollection = "bad$name" },
        { "at least the key", d => d.Columns.Clear() },
        { "not a valid document field", d => d.Columns[1].Field = "a.b" },
        { "not a valid document field", d => d.Columns[1].Field = "$set" },
        { "mapped 2 times", d => d.Columns.Add(new ColumnMapping { Column = "other", Field = "Name" }) },
        { "must be mapped to the field '_id'", d => d.Columns[0].Field = "Id" },
        { "must be mapped with Type Integer", d => d.Columns[0].Type = FieldType.String },
        { "only the key column may map to '_id'", d => d.Columns.Add(new ColumnMapping { Column = "name2", Field = "_id" }) },
    };

    [Theory]
    [MemberData(nameof(Breakages))]
    public void Each_kind_of_mistake_is_reported(string expected, Action<MigrationDefinition> break_)
    {
        var definition = Valid();
        break_(definition);

        var result = Validate(definition);

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(expected, Case.Insensitive);
    }

    [Fact]
    public void A_name_used_twice_is_reported() =>
        Validate(Valid(), Valid()).FailureMessage!.ShouldContain("lists 'hotels-from-sqlite' 2 times");

    [Fact]
    public void A_string_key_must_be_mapped_as_a_string()
    {
        var definition = Valid();
        definition.KeyType = KeyKind.String;

        Validate(definition).FailureMessage!.ShouldContain("must be mapped with Type String");
    }
}
