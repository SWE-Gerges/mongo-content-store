using Content.Mongo.Coordination;
using Content.Sync;
using Content.Sync.Configuration;
using Content.Sync.Search;
using Content.Sync.State;
using Content.TestSupport;
using Content.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Content.Tests.Sync;

/// <summary>Everything shared between instances: the stores, the clock and the sync pipeline (standing in for MongoDB and OpenSearch).</summary>
internal sealed class CoordinationHarness
{
    public CoordinationHarness()
    {
        Sync = new SyncHarness();
        Leases = new InMemoryLeaseStore(Sync.Clock);
        Runs = new InMemoryRunStore();
    }

    public SyncHarness Sync { get; }

    public InMemoryLeaseStore Leases { get; }

    public InMemoryRunStore Runs { get; }

    public CoordinationHarness WithHotels(int count, string collection = SyncHarness.Collection)
    {
        Sync.WithHotels(count, collection);
        return this;
    }

    public Node NewNode(TimeSpan? lease = null, TimeSpan? heartbeat = null) =>
        new(this, lease ?? TimeSpan.FromSeconds(15), heartbeat ?? TimeSpan.FromSeconds(5));
}

/// <summary>One API instance: its own coordinator and run host over the shared stores.</summary>
internal sealed class Node : IAsyncDisposable
{
    public Node(CoordinationHarness harness, TimeSpan lease, TimeSpan heartbeat)
    {
        Identity = new InstanceIdentity();
        Coordinator = new RunCoordinator(
            harness.Leases,
            harness.Runs,
            Options.Create(new CoordinationOptions { LeaseDuration = lease, HeartbeatInterval = heartbeat }),
            harness.Sync.Clock,
            Identity);
        Host = new RunHost(Coordinator, [new SyncRunWorker(harness.Sync.Runner)], NullLogger<RunHost>.Instance);
        Facade = new SyncCoordinator(Coordinator, harness.Sync.Checkpoints, harness.Sync.DeadLetters, Options.Create(harness.Sync.Fast(100)));
    }

    public InstanceIdentity Identity { get; }

    public RunCoordinator Coordinator { get; }

    public RunHost Host { get; }

    public ISyncCoordinator Facade { get; }

    public Task StartAsync(CancellationToken cancellationToken) => Host.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync(CancellationToken.None);
        Host.Dispose();
    }
}

[Collection(ClassMapCollection.Name)]
public sealed class SyncCoordinatorTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly CoordinationHarness _h = new();
    private readonly Node _node;

    public SyncCoordinatorTests()
    {
        _ = TestMappings.Counter;
        _node = _h.NewNode();
    }

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private SyncOptions RunOptions() => _node.Facade.CreateRunOptions(null, null);

    private async Task<SyncStatus> WaitForStateAsync(string collection, Func<SyncStatus, bool> done, ISyncCoordinator? facade = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = await (facade ?? _node.Facade).GetStatusAsync(collection, _ct);
            if (done(status))
            {
                return status;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"timed out; last state was {status.State}");
            await Task.Delay(5, _ct);
        }
    }

    [Fact]
    public async Task A_collection_that_never_ran_is_idle()
    {
        var status = await _node.Facade.GetStatusAsync("hotels", _ct);

        status.State.ShouldBe(SyncState.Idle);
        status.RunId.ShouldBeNull();
        status.Kind.ShouldBeNull();
        status.Indexed.ShouldBe(0);
    }

    [Fact]
    public async Task A_started_run_executes_in_the_background_and_reports_its_counts()
    {
        _h.WithHotels(250);
        await _node.StartAsync(_ct);

        var started = await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        var status = await WaitForStateAsync("hotels", s => s.State is SyncState.Completed or SyncState.Failed);

        started.Outcome.ShouldBe(SyncStartOutcome.Started);
        status.State.ShouldBe(SyncState.Completed);
        status.Kind.ShouldBe("sync");
        status.RunId.ShouldBe(started.RunId);
        status.Indexed.ShouldBe(250);
        status.LastId.ShouldBe("250");
        status.StartedAt.ShouldNotBeNull();
        status.FinishedAt.ShouldNotBeNull();
        status.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Only_one_run_per_collection_is_active_at_a_time()
    {
        _h.WithHotels(150);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Sync.Source.BeforeRead = async (_, _, token) => await release.Task.WaitAsync(token);
        await _node.StartAsync(_ct);

        var first = await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        await WaitForStateAsync("hotels", s => s.State == SyncState.Running);
        var second = await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);

        second.Outcome.ShouldBe(SyncStartOutcome.AlreadyRunning);
        second.RunId.ShouldBe(first.RunId);

        release.SetResult();
        await WaitForStateAsync("hotels", s => s.State == SyncState.Completed);
        var third = await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);

        third.Outcome.ShouldBe(SyncStartOutcome.Started);
        third.RunId.ShouldNotBe(first.RunId);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_the_collection_can_be_started_again()
    {
        _h.WithHotels(300);
        var blocked = true;
        _h.Sync.Source.BeforeRead = async (_, read, token) =>
        {
            if (read == 2 && blocked)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        await _node.StartAsync(_ct);

        await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        await WaitForStateAsync("hotels", s => s.State == SyncState.Running && s.Indexed == 100);

        (await _node.Facade.CancelAsync("hotels", _ct)).ShouldBeTrue();
        var status = await WaitForStateAsync("hotels", s => s.State == SyncState.Cancelled);

        status.LastId.ShouldBe("100");
        _h.Sync.Checkpoints.Current("hotels").ShouldBe(SyncHarness.Id(100));
        (await _node.Facade.CancelAsync("hotels", _ct)).ShouldBeFalse("nothing is running any more");

        blocked = false;
        (await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct)).Outcome.ShouldBe(SyncStartOutcome.Started);
        var finished = await WaitForStateAsync("hotels", s => s.State == SyncState.Completed);
        finished.Indexed.ShouldBe(200, "the new run resumes from the checkpoint at 100");
        _h.Sync.Index.Ids(_h.Sync.IndexName()).Count.ShouldBe(300);
    }

    [Fact]
    public async Task Cancelling_an_idle_collection_reports_that_nothing_was_running() =>
        (await _node.Facade.CancelAsync("hotels", _ct)).ShouldBeFalse();

    [Fact]
    public async Task A_failed_run_reports_the_error_and_where_it_stopped()
    {
        _h.WithHotels(300);
        _h.Sync.Writer.ThrowWhen = (call, _) => call == 2 ? new SearchIndexException("mapping conflict") : null;
        await _node.StartAsync(_ct);

        await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        var status = await WaitForStateAsync("hotels", s => s.State is SyncState.Failed or SyncState.Completed);

        status.State.ShouldBe(SyncState.Failed);
        status.LastError.ShouldNotBeNull().ShouldContain("mapping conflict");
        status.LastId.ShouldBe("100");
    }

    [Fact]
    public async Task Status_includes_the_dead_letters_stored_for_the_collection()
    {
        _h.WithHotels(30);
        _h.Sync.Writer.RejectWhen = d => d.Id == "3" ? new BulkItemFailure("3", 400, "bad", false) : null;
        await _node.StartAsync(_ct);

        await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        var status = await WaitForStateAsync("hotels", s => s.State == SyncState.Completed);

        status.DeadLettered.ShouldBe(1);
        status.DeadLettersTotal.ShouldBe(1);
    }

    [Fact]
    public async Task Runs_for_different_collections_proceed_in_parallel()
    {
        _h.WithHotels(150).WithHotels(150, "hotel_supplier-a");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        _h.Sync.Source.BeforeRead = async (_, read, token) =>
        {
            if (read > 2)
            {
                return;
            }

            if (Interlocked.Increment(ref concurrent) == 2)
            {
                started.TrySetResult();
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        };
        await _node.StartAsync(_ct);

        await _node.Facade.TryStartAsync("hotels", RunOptions(), _ct);
        await _node.Facade.TryStartAsync("hotel_supplier-a", RunOptions(), _ct);

        (await WaitForStateAsync("hotels", s => s.State is SyncState.Completed or SyncState.Failed)).State.ShouldBe(SyncState.Completed);
        (await WaitForStateAsync("hotel_supplier-a", s => s.State is SyncState.Completed or SyncState.Failed)).State.ShouldBe(SyncState.Completed);
        _h.Sync.Index.IndexNames.Order().ShouldBe(["content-hotel_supplier-a", "content-hotels"]);
    }

    [Fact]
    public void Run_options_apply_overrides_on_top_of_the_configured_defaults()
    {
        var options = _node.Facade.CreateRunOptions(250, TimeSpan.FromSeconds(2));

        options.BatchSize.ShouldBe(250);
        options.InterBatchDelay.ShouldBe(TimeSpan.FromSeconds(2));
        options.BulkChunkSize.ShouldBe(100);
        _node.Facade.CreateRunOptions(null, null).BatchSize.ShouldBe(100);
    }

    [Fact]
    public async Task A_start_that_cannot_write_its_run_record_releases_the_lease_again()
    {
        var harness = new CoordinationHarness();
        var coordinator = new RunCoordinator(
            harness.Leases,
            new ThrowingRunStore(),
            Options.Create(new CoordinationOptions()),
            harness.Sync.Clock,
            new InstanceIdentity());

        await Should.ThrowAsync<InvalidOperationException>(() => coordinator.TryStartAsync(RunKinds.Sync, "hotels", null, _ct));

        (await harness.Leases.GetAsync("hotels", _ct)).ShouldBeNull("a failed start must not leave the collection locked");
    }

    private sealed class ThrowingRunStore : IRunStore
    {
        public Task CreateAsync(RunRecord record, CancellationToken cancellationToken) => throw new InvalidOperationException("store down");

        public Task MarkRunningAsync(string runId, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateProgressAsync(string runId, IReadOnlyDictionary<string, long> counters, string? lastId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FinishAsync(string runId, RunState state, string? error, BsonDocument? detail, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<RunRecord?>(null);

        public Task<RunRecord?> GetLatestAsync(string target, CancellationToken cancellationToken) => Task.FromResult<RunRecord?>(null);

        public Task MarkInterruptedAsync(string target, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
