using Content.Migration;
using Content.Mongo.Coordination;
using Content.Sync.Checkpoints;
using Content.Sync.DeadLetters;
using Content.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace Content.Tests.Migration;

public class MigrationRunnerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static MigrationDefinition Definition() => new SqliteSourceShape().Definition();

    private sealed class Harness
    {
        public Harness()
        {
            Checkpoints = new InMemoryCheckpoints();
            DeadLetters = new InMemoryDeadLetters();
            Runner = new MigrationRunner(Source, Target, Checkpoints, DeadLetters, NullLogger<MigrationRunner>.Instance);
        }

        public InMemoryRowSource Source { get; } = new();

        public InMemoryMigrationTarget Target { get; } = new();

        public InMemoryCheckpoints Checkpoints { get; }

        public InMemoryDeadLetters DeadLetters { get; }

        public MigrationRunner Runner { get; }

        public string Key => Definition().Target;

        public Task<MigrationSummary> RunAsync(int batchSize = 100, MigrationHooks? hooks = null) =>
            Runner.RunAsync(Definition(), batchSize, TestContext.Current.CancellationToken, hooks, "run-1");

        public Task<MigrationSummary> RunAsync(int batchSize, CancellationToken cancellationToken) =>
            Runner.RunAsync(Definition(), batchSize, cancellationToken, null, "run-1");
    }

    /// <summary>The definition without a database behind it.</summary>
    private sealed class SqliteSourceShape
    {
        public MigrationDefinition Definition() => new()
        {
            Name = "hotels-from-sqlite",
            Provider = "sqlite",
            ConnectionString = "Data Source=unused",
            Query = "SELECT id, code, name, city_code, rating, group_id FROM hotels",
            KeyColumn = "id",
            TargetCollection = "hotels",
            Columns =
            [
                new ColumnMapping { Column = "id", Field = "_id", Type = FieldType.Integer, Required = true },
                new ColumnMapping { Column = "code", Field = "Code" },
                new ColumnMapping { Column = "name", Field = "Name", Required = true },
                new ColumnMapping { Column = "city_code", Field = "CityCode" },
                new ColumnMapping { Column = "rating", Field = "Rating", Type = FieldType.Double },
                new ColumnMapping { Column = "group_id", Field = "GroupId", Type = FieldType.StringOrNumber },
            ],
        };
    }

    [Fact]
    public async Task A_full_run_writes_every_row_checkpoints_each_batch_clears_it_and_verifies_the_counts()
    {
        var h = new Harness();
        h.Source.WithRows(250);

        var summary = await h.RunAsync(100);

        summary.Migrated.ShouldBe(250);
        summary.Invalid.ShouldBe(0);
        summary.Batches.ShouldBe(3);
        summary.ResumedFromKey.ShouldBeNull();
        summary.Verification.Matches.ShouldBeTrue(summary.Verification.Describe());
        summary.Verification.SourceCount.ShouldBe(250);
        h.Target.Ids.Count.ShouldBe(250);
        h.Checkpoints.Saved.ShouldBe([new BsonInt64(100), new BsonInt64(200), new BsonInt64(250)]);
        h.Checkpoints.Current(h.Key).ShouldBeNull();
        h.Target.Get(7)!["Rating"].ShouldBe(new BsonDouble(4.5));
        h.Target.Get(7)!["GroupId"].ShouldBe(new BsonString("G7"));
        h.Target.Get(8)!["GroupId"].ShouldBe(new BsonString("8"), "a numeric group id is stored as text like the rest");
    }

    [Fact]
    public async Task A_crash_midway_resumes_after_the_checkpoint_losing_nothing_and_duplicating_nothing()
    {
        var h = new Harness();
        h.Source.WithRows(1000);
        h.Target.FailUpsertWhen = call => call == 4 ? new IOException("target went away") : null;

        await Should.ThrowAsync<IOException>(() => h.RunAsync(100));

        h.Checkpoints.Current(h.Key).ShouldBe(new BsonInt64(300), "three batches were settled before the fourth write failed");
        h.Target.Ids.Count.ShouldBe(300);

        h.Target.FailUpsertWhen = null;
        var resumed = await h.RunAsync(100);

        resumed.ResumedFromKey.ShouldBe("300");
        resumed.RowsRead.ShouldBe(700);
        resumed.Verification.Matches.ShouldBeTrue(resumed.Verification.Describe());
        h.Target.Ids.Count.ShouldBe(1000, "no duplicates");
        h.Target.Ids.Order().ShouldBe(Enumerable.Range(1, 1000).Select(i => i.ToString()).Order(), "no loss");
        h.Target.DocumentsWritten.ShouldBe(1000, "the resume did not rewrite the finished batches");
        h.Checkpoints.Current(h.Key).ShouldBeNull();
    }

    [Fact]
    public async Task Running_a_finished_migration_again_changes_nothing()
    {
        var h = new Harness();
        h.Source.WithRows(120);
        await h.RunAsync(50);
        var before = h.Target.Ids.Order().ToList();
        var snapshot = before.ToDictionary(id => id, id => h.Target.Get(long.Parse(id))!.ToJson());

        var second = await h.RunAsync(50);

        second.Verification.Matches.ShouldBeTrue();
        h.Target.Ids.Order().ShouldBe(before);
        before.ShouldAllBe(id => h.Target.Get(long.Parse(id))!.ToJson() == snapshot[id], "every document is exactly as it was");
        h.Target.Ids.Count.ShouldBe(120);
    }

    [Fact]
    public async Task Invalid_rows_are_dead_lettered_and_the_run_carries_on()
    {
        var h = new Harness();
        h.Source.Add(
            InMemoryRowSource.Row(1),
            InMemoryRowSource.Row(2, name: " "),
            InMemoryRowSource.Row(3, rating: "five stars"),
            InMemoryRowSource.Row(4),
            InMemoryRowSource.Row(5, rating: "3.5"));

        var summary = await h.RunAsync(2);

        summary.Migrated.ShouldBe(3);
        summary.Invalid.ShouldBe(2);
        h.Target.Ids.Order().ShouldBe(["1", "4", "5"]);
        h.Target.Get(5)!["Rating"].ShouldBe(new BsonDouble(3.5), "text that reads as a number is converted");
        var letters = await h.DeadLetters.ListAsync(h.Key, null, 10, _ct);
        letters.Items.Select(l => l.DocId).Order().ShouldBe(["2", "3"]);
        letters.Items.Single(l => l.DocId == "2").Reason.ShouldContain("column 'name' is required but empty");
        letters.Items.Single(l => l.DocId == "3").Reason.ShouldContain("column 'rating' value 'five stars' cannot be read as Double");
        letters.Items.ShouldAllBe(l => l.PayloadHash.Length == 64);
        summary.Verification.Matches.ShouldBeTrue("the two dead-lettered rows account for the difference: " + summary.Verification.Describe());
    }

    [Fact]
    public async Task Migration_dead_letters_stay_apart_from_sync_dead_letters()
    {
        var h = new Harness();
        h.Source.Add(InMemoryRowSource.Row(1, name: ""));
        await h.DeadLetters.WriteAsync("hotels", [new DeadLetterEntry("1", "bulk-rejected", "h")], null, _ct);

        await h.RunAsync();

        (await h.DeadLetters.CountAsync("hotels", null, _ct)).ShouldBe(1, "the sync's own dead letter for the same id is untouched");
        (await h.DeadLetters.CountAsync(h.Key, null, _ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_row_that_was_fixed_in_the_source_is_no_longer_outstanding_on_the_next_run()
    {
        var h = new Harness();
        h.Source.Add(InMemoryRowSource.Row(1), InMemoryRowSource.Row(2, name: ""));
        await h.RunAsync();
        (await h.DeadLetters.CountAsync(h.Key, null, _ct)).ShouldBe(1);

        var fixedSource = new InMemoryRowSource().Add(InMemoryRowSource.Row(1), InMemoryRowSource.Row(2));
        var runner = new MigrationRunner(fixedSource, h.Target, h.Checkpoints, h.DeadLetters, NullLogger<MigrationRunner>.Instance);
        var again = await runner.RunAsync(Definition(), 100, _ct);

        (await h.DeadLetters.CountAsync(h.Key, null, _ct)).ShouldBe(0);
        again.Verification.Matches.ShouldBeTrue(again.Verification.Describe());
        h.Target.Ids.Order().ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task A_count_mismatch_is_reported_with_the_numbers_not_hidden()
    {
        var h = new Harness();
        h.Source.WithRows(100);
        h.Target.SilentlyDropWhen = document => document["_id"].AsInt64 % 10 == 0;

        var summary = await h.RunAsync();

        summary.Verification.Matches.ShouldBeFalse();
        summary.Verification.SourceCount.ShouldBe(100);
        summary.Verification.TargetCount.ShouldBe(90);
        summary.Verification.ExpectedTarget.ShouldBe(100);
        summary.Verification.Difference.ShouldBe(-10);
        summary.Verification.Describe().ShouldContain("should hold 100 but holds 90");
    }

    [Fact]
    public async Task Rows_with_a_null_key_are_never_read_and_the_count_check_exposes_them()
    {
        var h = new Harness();
        h.Source.WithRows(10);
        h.Source.NullKeyRows = 3;

        var summary = await h.RunAsync();

        summary.Migrated.ShouldBe(10);
        summary.Verification.Matches.ShouldBeFalse();
        summary.Verification.Difference.ShouldBe(-3);
    }

    [Fact]
    public async Task Documents_already_in_the_target_that_the_source_does_not_have_show_up_as_a_mismatch()
    {
        var h = new Harness();
        h.Source.WithRows(5);
        h.Target.Preload(new BsonDocument("_id", 999L));

        var summary = await h.RunAsync();

        summary.Verification.Matches.ShouldBeFalse();
        summary.Verification.Difference.ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_keeps_the_last_completed_checkpoint()
    {
        var h = new Harness();
        h.Source.WithRows(1000);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        h.Source.BeforeRead = (read, _) =>
        {
            if (read == 4)
            {
                cancel.Cancel();
            }

            return Task.CompletedTask;
        };

        var failure = await Should.ThrowAsync<OperationCanceledException>(() => h.RunAsync(100, cancel.Token));

        failure.ShouldBeAssignableTo<OperationCanceledException>();
        h.Checkpoints.Current(h.Key).ShouldBe(new BsonInt64(300));
        h.Target.Ids.Count.ShouldBe(300);
    }

    [Fact]
    public async Task A_deposed_run_never_writes_a_checkpoint()
    {
        var h = new Harness();
        h.Source.WithRows(100);
        var hooks = new MigrationHooks { BeforeCheckpoint = _ => Task.FromException(new LeaseLostException(h.Key)) };

        await Should.ThrowAsync<LeaseLostException>(() => h.RunAsync(50, hooks));

        h.Checkpoints.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_is_reported_after_each_batch()
    {
        var h = new Harness();
        h.Source.WithRows(250);
        var reports = new List<MigrationProgress>();
        var hooks = new MigrationHooks { OnProgress = (progress, _) => { reports.Add(progress); return Task.CompletedTask; } };

        await h.RunAsync(100, hooks);

        reports.Select(r => (r.Migrated, r.Batches, r.LastKey)).ShouldBe([(100L, 1L, "100"), (200L, 2L, "200"), (250L, 3L, "250")]);
    }

    [Fact]
    public async Task A_checkpoint_of_the_wrong_key_type_is_refused_instead_of_misread()
    {
        var h = new Harness();
        h.Source.WithRows(10);
        await h.Checkpoints.SaveAsync(h.Key, new BsonString("abc"), "old", _ct);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => h.RunAsync());

        failure.Message.ShouldContain("holds a String key");
    }

    [Fact]
    public async Task A_large_source_is_read_and_written_a_page_at_a_time_never_all_at_once()
    {
        var h = new Harness();
        h.Source.WithRows(20_000);

        var summary = await h.RunAsync(250);

        summary.Migrated.ShouldBe(20_000);
        h.Source.LargestPage.ShouldBeLessThanOrEqualTo(250, "no page is larger than the batch size");
        h.Target.LargestWrite.ShouldBeLessThanOrEqualTo(250, "no write is larger than the batch size");
        h.Source.Reads.ShouldBe(80, "one read per page");
        summary.Verification.Matches.ShouldBeTrue();
    }
}
