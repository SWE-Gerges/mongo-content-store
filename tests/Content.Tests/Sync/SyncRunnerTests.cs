using Content.Sync.Search;
using Content.Tests.Support;
using MongoDB.Bson;

namespace Content.Tests.Sync;

[Collection(ClassMapCollection.Name)]
public class SyncRunnerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SyncRunnerTests() => _ = TestMappings.Counter;

    [Fact]
    public async Task A_full_run_indexes_every_document_checkpoints_each_batch_and_clears_the_checkpoint()
    {
        var h = new SyncHarness().WithHotels(250);

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(100), _ct);

        summary.Indexed.ShouldBe(250);
        summary.Batches.ShouldBe(3);
        summary.DeadLettered.ShouldBe(0);
        summary.ResumedFromId.ShouldBeNull();
        h.Index.Ids(h.IndexName()).ShouldBe(SyncHarness.IdStrings(1, 250), ignoreOrder: true);
        h.Checkpoints.Saved.ShouldBe([SyncHarness.Id(100), SyncHarness.Id(200), SyncHarness.Id(250)]);
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBeNull();
        h.Index.IndexNames.ShouldBe(["content-hotels"]);
    }

    [Fact]
    public async Task An_empty_collection_completes_without_a_checkpoint()
    {
        var h = new SyncHarness().WithHotels(0);

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(0);
        summary.Batches.ShouldBe(0);
        h.Checkpoints.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task Crash_and_resume_loses_nothing_and_duplicates_nothing()
    {
        var h = new SyncHarness().WithHotels(1000);
        h.Writer.ThrowWhen = (call, _) => call == 4 ? new SearchIndexUnavailableException("simulated crash") : null;
        var options = h.Fast(100);
        options.MaxBulkAttempts = 1;

        await Should.ThrowAsync<SearchIndexUnavailableException>(() => h.Runner.RunAsync(SyncHarness.Collection, options, _ct));

        // Batches 1-3 were checkpointed, the failed fourth was not.
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBe(SyncHarness.Id(300));
        h.Index.Ids(h.IndexName()).ShouldBe(SyncHarness.IdStrings(1, 300), ignoreOrder: true);

        h.Writer.ThrowWhen = null;
        var resumed = await h.Runner.RunAsync(SyncHarness.Collection, options, _ct);

        resumed.ResumedFromId.ShouldBe("300");
        resumed.Indexed.ShouldBe(700);
        h.Index.Ids(h.IndexName()).ShouldBe(SyncHarness.IdStrings(1, 1000), ignoreOrder: true);
        h.Index.IndexOperations.ShouldBe(1000, "the resume must not re-index batches that were already checkpointed");
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBeNull();
    }

    [Fact]
    public async Task A_completed_run_leaves_the_next_run_to_rescan_everything_idempotently()
    {
        var h = new SyncHarness().WithHotels(120);
        await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        var again = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        again.ResumedFromId.ShouldBeNull();
        again.Indexed.ShouldBe(120);
        h.Index.Ids(h.IndexName()).Count.ShouldBe(120);
    }

    [Fact]
    public async Task A_transient_failure_that_survives_the_retries_aborts_the_run_and_the_checkpoint_does_not_advance()
    {
        var h = new SyncHarness().WithHotels(300);
        h.Writer.ThrowWhen = (call, _) => call >= 2 ? new SearchIndexUnavailableException("cluster down") : null;

        var failure = await Should.ThrowAsync<SearchIndexUnavailableException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct));

        failure.Message.ShouldBe("cluster down");
        h.Writer.BulkCalls.ShouldBe(1 + 3, "batch two is attempted three times before the run gives up");
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBe(SyncHarness.Id(100));
        h.Checkpoints.Saved.ShouldBe([SyncHarness.Id(100)]);
        h.DeadLetters.Entries.ShouldBeEmpty("an outage must never turn healthy documents into dead letters");
    }

    [Fact]
    public async Task A_transient_failure_inside_the_retry_budget_is_absorbed()
    {
        var h = new SyncHarness().WithHotels(200);
        h.Writer.ThrowWhen = (call, _) => call is 2 or 3 ? new SearchIndexUnavailableException("blip") : null;

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(200);
        h.Index.Ids(h.IndexName()).Count.ShouldBe(200);
    }

    [Fact]
    public async Task A_permanent_request_level_failure_is_not_retried()
    {
        var h = new SyncHarness().WithHotels(200);
        h.Writer.ThrowWhen = (call, _) => call == 2 ? new SearchIndexException("forbidden") : null;

        await Should.ThrowAsync<SearchIndexException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct));

        h.Writer.BulkCalls.ShouldBe(2);
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBe(SyncHarness.Id(100));
    }

    [Fact]
    public async Task Rejected_documents_are_dead_lettered_before_the_checkpoint_moves_past_their_batch()
    {
        var h = new SyncHarness().WithHotels(30);
        h.Writer.RejectWhen = document => document.Id is "5" or "17"
            ? new BulkItemFailure(document.Id, 400, "mapper_parsing_exception: illegal latitude", Retryable: false)
            : null;

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(10), _ct);

        summary.Indexed.ShouldBe(28);
        summary.DeadLettered.ShouldBe(2);
        h.Index.Ids(h.IndexName()).ShouldBe(SyncHarness.IdStrings(1, 30).Except(["5", "17"]), ignoreOrder: true);
        h.Log.Entries.ShouldBe(
        [
            "deadletter:hotels:5",
            "checkpoint:hotels:10",
            "deadletter:hotels:17",
            "checkpoint:hotels:20",
            "checkpoint:hotels:30",
            "checkpoint-deleted:hotels",
        ]);
        var letter = h.DeadLetters.Entries["hotels|5"];
        letter.Reason.ShouldContain("mapper_parsing_exception");
        letter.PayloadHash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task If_the_dead_letter_write_fails_the_run_aborts_and_the_checkpoint_stays_behind()
    {
        var h = new SyncHarness().WithHotels(30);
        h.Writer.RejectWhen = document => document.Id == "15" ? new BulkItemFailure("15", 400, "mapper_parsing_exception", false) : null;
        h.DeadLetters.FailWriteWhen = _ => new InvalidOperationException("dead-letter store down");

        await Should.ThrowAsync<InvalidOperationException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(10), _ct));

        h.Checkpoints.Saved.ShouldBe([SyncHarness.Id(10)], "batch two must not be checkpointed without its dead letter");
    }

    [Fact]
    public async Task A_retryable_rejection_is_retried_once_and_then_indexed()
    {
        var h = new SyncHarness().WithHotels(30);
        var rejectedOnce = false;
        h.Writer.RejectWhen = document =>
        {
            if (document.Id != "7" || rejectedOnce)
            {
                return null;
            }

            rejectedOnce = true;
            return new BulkItemFailure("7", 429, "es_rejected_execution_exception", Retryable: true);
        };

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(30), _ct);

        summary.Indexed.ShouldBe(30);
        summary.DeadLettered.ShouldBe(0);
        h.Writer.BulkCalls.ShouldBe(2, "one request for the batch and one for the retried item");
    }

    [Fact]
    public async Task An_item_that_stays_retryable_after_its_retry_aborts_the_run_instead_of_being_lost()
    {
        var h = new SyncHarness().WithHotels(30);
        h.Writer.RejectWhen = document => document.Id == "25" ? new BulkItemFailure("25", 503, "unavailable_shards_exception", Retryable: true) : null;

        await Should.ThrowAsync<BulkRetriesExhaustedException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(10), _ct));

        h.Checkpoints.Saved.ShouldBe([SyncHarness.Id(10), SyncHarness.Id(20)]);
        h.DeadLetters.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_that_cannot_be_decoded_is_dead_lettered_by_its_raw_id_and_the_run_continues()
    {
        var h = new SyncHarness();
        h.Source.Add(SyncHarness.Collection, [.. InMemorySource.Hotels([1, 2, 3]), new BsonDocument { { "_id", "not-a-number" }, { "Name", "Bad id" } }]);

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(3);
        summary.DeadLettered.ShouldBe(1);
        h.Index.Ids(h.IndexName()).ShouldBe(["1", "2", "3"], ignoreOrder: true);
        var letter = h.DeadLetters.Entries["hotels|not-a-number"];
        letter.Reason.ShouldStartWith("deserialization-failed:");
        letter.PayloadHash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_keeps_the_last_completed_checkpoint()
    {
        var h = new SyncHarness().WithHotels(300);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        h.Source.BeforeRead = (_, read, _) =>
        {
            if (read == 3)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };

        var failure = await Record.ExceptionAsync(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), cts.Token));

        failure.ShouldBeAssignableTo<OperationCanceledException>();
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBe(SyncHarness.Id(200));
    }

    [Fact]
    public async Task Progress_is_reported_after_each_batch()
    {
        var h = new SyncHarness().WithHotels(250);
        var reports = new List<Content.Sync.SyncProgress>();

        await h.Runner.RunAsync(
            SyncHarness.Collection,
            h.Fast(100),
            _ct,
            new Content.Sync.SyncRunHooks { OnProgress = (progress, _) => { reports.Add(progress); return Task.CompletedTask; } },
            "run-1");

        reports.Select(r => (r.RunId, r.Indexed, r.Batches, r.LastId)).ShouldBe(
        [
            ("run-1", 100L, 1L, "100"),
            ("run-1", 200L, 2L, "200"),
            ("run-1", 250L, 3L, "250"),
        ]);
    }
}

[Collection(ClassMapCollection.Name)]
public class SyncRunnerCollisionTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SyncRunnerCollisionTests() => _ = TestMappings.Counter;

    [Fact]
    public async Task Ids_that_a_lossy_conversion_would_merge_stay_distinct_documents()
    {
        // 2^53 and 2^53 + 1 are the same double; long.MaxValue and long.MaxValue - 1 likewise.
        long[] ids = [9_007_199_254_740_992, 9_007_199_254_740_993, 9_007_199_254_740_994, long.MaxValue, long.MaxValue - 1, 0, 1, 10, 100, -1, -10, long.MinValue + 1];
        var h = new SyncHarness();
        h.Source.Add(SyncHarness.Collection, InMemorySource.Hotels(ids));

        await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        h.Index.Ids(h.IndexName()).ShouldBe(ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ignoreOrder: true);
        h.Index.Ids(h.IndexName()).Count.ShouldBe(ids.Length);
    }
}
