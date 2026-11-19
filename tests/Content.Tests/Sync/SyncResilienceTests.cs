using Content.Sync.Configuration;
using Content.Sync.Search;
using Content.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Content.Tests.Sync;

[Collection(ClassMapCollection.Name)]
public class SyncResilienceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SyncResilienceTests() => _ = TestMappings.Counter;

    private SyncOptions Options(SyncHarness h, int attempts, TimeSpan baseDelay, TimeSpan cap)
    {
        var options = h.Fast();
        options.MaxReadAttempts = attempts;
        options.ReadBackoffBase = baseDelay;
        options.ReadBackoffCap = cap;
        return options;
    }

    /// <summary>Lets time pass whenever the code under test is waiting on a timer, until the task finishes.</summary>
    private async Task<Exception?> RunAdvancingTimeAsync(SyncHarness h, Func<Task> run)
    {
        var task = run();
        var handled = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var timers = h.Clock.DueTimes;
            if (timers.Count > handled)
            {
                h.Clock.Inner.Advance(timers[handled]);
                handled++;
            }
            else
            {
                await Task.Delay(1, _ct);
            }
        }

        task.IsCompleted.ShouldBeTrue("the run should finish once time is allowed to pass");
        return await Record.ExceptionAsync(() => task);
    }

    [Fact]
    public async Task A_read_that_fails_transiently_is_retried_and_the_run_completes()
    {
        var h = new SyncHarness().WithHotels(150);
        h.Source.BeforeRead = (_, read, _) => read <= 2 ? Task.FromException(new TimeoutException("mongo slow")) : Task.CompletedTask;

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(150);
        h.Source.Reads.ShouldBe(2 + 2);
    }

    [Fact]
    public async Task Read_retries_stop_at_the_attempt_cap_and_surface_the_failure_without_advancing_the_checkpoint()
    {
        var h = new SyncHarness().WithHotels(300);
        h.Source.BeforeRead = (_, read, _) => read >= 2 ? Task.FromException(new TimeoutException("mongo down")) : Task.CompletedTask;

        var failure = await Should.ThrowAsync<TimeoutException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(100), _ct));

        failure.Message.ShouldBe("mongo down");
        h.Source.Reads.ShouldBe(1 + 3, "one good read, then three attempts at the next page");
        h.Checkpoints.Current(SyncHarness.Collection).ShouldBe(SyncHarness.Id(100));
    }

    [Fact]
    public async Task Non_transient_read_errors_are_not_retried()
    {
        var h = new SyncHarness().WithHotels(100);
        h.Source.BeforeRead = (_, _, _) => Task.FromException(new InvalidOperationException("bug"));

        await Should.ThrowAsync<InvalidOperationException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct));

        h.Source.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Read_backoff_doubles_each_attempt()
    {
        var h = new SyncHarness().WithHotels(10);
        h.Source.BeforeRead = (_, _, _) => Task.FromException(new TimeoutException("down"));

        var failure = await RunAdvancingTimeAsync(h, () => h.Runner.RunAsync(SyncHarness.Collection, Options(h, 5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)), _ct));

        failure.ShouldBeOfType<TimeoutException>();
        h.Clock.DueTimes.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)]);
        h.Source.Reads.ShouldBe(5);
    }

    [Fact]
    public async Task Read_backoff_is_capped()
    {
        var h = new SyncHarness().WithHotels(10);
        h.Source.BeforeRead = (_, _, _) => Task.FromException(new TimeoutException("down"));

        await RunAdvancingTimeAsync(h, () => h.Runner.RunAsync(SyncHarness.Collection, Options(h, 6, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)), _ct));

        h.Clock.DueTimes.ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)]);
    }

    [Fact]
    public async Task A_run_waiting_to_retry_a_read_is_cancellable_even_when_time_never_advances()
    {
        var h = new SyncHarness().WithHotels(10);
        h.Source.BeforeRead = (_, _, _) => Task.FromException(new TimeoutException("down"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        var run = h.Runner.RunAsync(SyncHarness.Collection, Options(h, 5, TimeSpan.FromHours(1), TimeSpan.FromSeconds(60)), cts.Token);
        while (h.Clock.DueTimes.Count == 0)
        {
            await Task.Delay(1, _ct);
        }

        await cts.CancelAsync();

        (await Record.ExceptionAsync(() => run)).ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task The_inter_batch_delay_is_honoured_between_batches_only()
    {
        var h = new SyncHarness().WithHotels(250);
        var options = h.Fast(100);
        options.InterBatchDelay = TimeSpan.FromSeconds(5);

        var failure = await RunAdvancingTimeAsync(h, () => h.Runner.RunAsync(SyncHarness.Collection, options, _ct));

        failure.ShouldBeNull();
        h.Clock.DueTimes.ShouldBe([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], "two gaps between three batches, none after the last");
    }

    [Fact]
    public async Task A_failed_run_can_be_resumed_after_a_read_outage_without_reindexing_finished_batches()
    {
        var h = new SyncHarness().WithHotels(300);
        h.Source.BeforeRead = (_, read, _) => read >= 3 ? Task.FromException(new TimeoutException("down")) : Task.CompletedTask;
        await Should.ThrowAsync<TimeoutException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(100), _ct));
        var indexedBefore = h.Index.IndexOperations;

        h.Source.BeforeRead = null;
        var resumed = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(100), _ct);

        indexedBefore.ShouldBe(200);
        resumed.ResumedFromId.ShouldBe("200");
        resumed.Indexed.ShouldBe(100);
        h.Index.IndexOperations.ShouldBe(300);
    }
}

[Collection(ClassMapCollection.Name)]
public class IndexCreationRetryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public IndexCreationRetryTests() => _ = TestMappings.Counter;

    [Fact]
    public async Task Creating_the_index_is_retried_while_the_cluster_is_unavailable_and_then_the_run_proceeds()
    {
        var h = new SyncHarness().WithHotels(120);
        h.Writer.EnsureThrowWhen = call => call <= 2 ? new SearchIndexUnavailableException("cluster starting") : null;

        var summary = await h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct);

        summary.Indexed.ShouldBe(120);
        h.Writer.EnsureCalls.ShouldBe(3);
    }

    [Fact]
    public async Task The_retries_are_bounded_and_the_run_fails_before_reading_anything()
    {
        var h = new SyncHarness().WithHotels(120);
        h.Writer.EnsureThrowWhen = _ => new SearchIndexUnavailableException("cluster down");

        await Should.ThrowAsync<SearchIndexUnavailableException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct));

        h.Writer.EnsureCalls.ShouldBe(3, "MaxBulkAttempts is the shared request budget");
        h.Source.Reads.ShouldBe(0);
        h.Checkpoints.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_permanent_refusal_is_not_retried()
    {
        var h = new SyncHarness().WithHotels(10);
        h.Writer.EnsureThrowWhen = _ => new SearchIndexException("forbidden");

        await Should.ThrowAsync<SearchIndexException>(() => h.Runner.RunAsync(SyncHarness.Collection, h.Fast(), _ct));

        h.Writer.EnsureCalls.ShouldBe(1);
    }

    [Fact]
    public async Task The_backoff_between_attempts_is_jittered_within_the_cap_and_cancellable()
    {
        var h = new SyncHarness().WithHotels(10);
        h.Writer.EnsureThrowWhen = _ => new SearchIndexUnavailableException("cluster down");
        var options = h.Fast();
        options.MaxBulkAttempts = 5;
        options.BulkBackoffBase = TimeSpan.FromSeconds(10);
        options.BulkBackoffCap = TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        var run = h.Runner.RunAsync(SyncHarness.Collection, options, cts.Token);
        var advanced = 0;
        while (!run.IsCompleted && advanced < 2)
        {
            if (h.Clock.DueTimes.Count > advanced)
            {
                h.Clock.Inner.Advance(h.Clock.DueTimes[advanced]);
                advanced++;
            }
            else
            {
                await Task.Delay(1, _ct);
            }
        }

        while (h.Clock.DueTimes.Count < 3)
        {
            await Task.Delay(1, _ct);
        }

        await cts.CancelAsync();
        (await Record.ExceptionAsync(() => run)).ShouldBeAssignableTo<OperationCanceledException>();
        h.Clock.DueTimes.Take(3).ShouldAllBe(delay => delay > TimeSpan.Zero && delay <= TimeSpan.FromSeconds(30), "waits are positive and never exceed the cap");
    }
}

public class DeadLetterCursorTests
{
    [Theory]
    [InlineData(0L, "a")]
    [InlineData(1_763_552_400_123L, "hotels|42")]
    [InlineData(1_763_552_400_123L, "id with\nnewline|and | pipes")]
    public void A_cursor_round_trips_the_millisecond_and_the_id(long milliseconds, string id)
    {
        var cursor = Content.Sync.DeadLetters.MongoDeadLetterStore.EncodeCursor(milliseconds, id);

        Content.Sync.DeadLetters.MongoDeadLetterStore.DecodeCursor(cursor).ShouldBe((milliseconds, id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("aGVsbG8")]
    public void A_foreign_cursor_is_rejected(string cursor) =>
        Should.Throw<Content.Store.InvalidCursorException>(() => Content.Sync.DeadLetters.MongoDeadLetterStore.DecodeCursor(cursor));
}

public class FakeClockSanityTests
{
    [Fact]
    public void The_recording_provider_delegates_time_to_the_fake_clock()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var recording = new RecordingTimeProvider(fake);

        fake.Advance(TimeSpan.FromMinutes(1));

        recording.GetUtcNow().ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
    }
}
