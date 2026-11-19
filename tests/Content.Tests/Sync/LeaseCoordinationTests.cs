using Content.Mongo.Coordination;
using Content.Sync.State;
using Content.Tests.Support;

namespace Content.Tests.Sync;

/// <summary>Behaviour of the per-collection lease across several instances sharing one set of stores.</summary>
[Collection(ClassMapCollection.Name)]
public sealed class LeaseCoordinationTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly CoordinationHarness _h = new();
    private readonly List<Node> _nodes = [];

    public LeaseCoordinationTests() => _ = TestMappings.Counter;

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }
    }

    private Node NewNode()
    {
        var node = _h.NewNode();
        _nodes.Add(node);
        return node;
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, $"timed out waiting for {what}");
            await Task.Delay(5, _ct);
        }
    }

    private Task WaitForAsync(Node node, SyncState state, Func<Content.Sync.State.SyncStatus, bool>? extra = null) =>
        WaitUntilAsync(
            async () =>
            {
                var status = await node.Facade.GetStatusAsync("hotels", _ct);
                return status.State == state && (extra?.Invoke(status) ?? true);
            },
            $"state {state}");

    private Task HeartbeatTimerExistsAsync() => WaitUntilAsync(() => Task.FromResult(_h.Sync.Clock.DueTimes.Count > 0), "the heartbeat timer");

    private void Tick(TimeSpan by) => _h.Sync.Clock.Inner.Advance(by);

    private Task<Content.Sync.State.SyncStartResult> Start(Node node) =>
        node.Facade.TryStartAsync("hotels", node.Facade.CreateRunOptions(null, null), _ct);

    /// <summary>Makes the worker stop after its first batch so the run stays active until released.</summary>
    private TaskCompletionSource HoldAfterFirstBatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Sync.Source.BeforeRead = async (_, read, token) =>
        {
            if (read == 2)
            {
                await release.Task.WaitAsync(token);
            }
        };
        return release;
    }

    [Fact]
    public async Task Instances_racing_for_one_collection_produce_exactly_one_start()
    {
        var nodes = Enumerable.Range(0, 8).Select(_ => NewNode()).ToList();

        var results = await Task.WhenAll(nodes.Select(node => Task.Run(() => Start(node), _ct)));

        results.Count(r => r.Outcome == SyncStartOutcome.Started).ShouldBe(1);
        var winner = results.Single(r => r.Outcome == SyncStartOutcome.Started);
        results.Where(r => r.Outcome == SyncStartOutcome.AlreadyRunning).ShouldAllBe(r => r.RunId == winner.RunId);
        _h.Runs.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_lease_hands_the_collection_over_and_the_orphaned_run_reads_as_interrupted()
    {
        var crashed = NewNode();
        var survivor = NewNode();
        var first = await Start(crashed);

        (await Start(survivor)).Outcome.ShouldBe(SyncStartOutcome.AlreadyRunning);

        // The owner never heartbeats (it is not running), so its lease lapses.
        Tick(TimeSpan.FromSeconds(16));
        (await survivor.Facade.GetStatusAsync("hotels", _ct)).State.ShouldBe(SyncState.Interrupted);
        var second = await Start(survivor);

        second.Outcome.ShouldBe(SyncStartOutcome.Started);
        second.RunId.ShouldNotBe(first.RunId);
        _h.Runs.All.Single(r => r.Id == first.RunId).State.ShouldBe(RunState.Interrupted);
    }

    [Fact]
    public async Task Heartbeats_extend_the_lease_so_a_long_run_is_never_taken_over()
    {
        _h.WithHotels(300);
        var release = HoldAfterFirstBatch();
        var owner = NewNode();
        var rival = NewNode();
        await owner.StartAsync(_ct);
        await Start(owner);
        await WaitForAsync(owner, SyncState.Running, s => s.Indexed == 100);
        await HeartbeatTimerExistsAsync();

        for (var beat = 1; beat <= 8; beat++)
        {
            var before = _h.Leases.Renewals;
            Tick(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => Task.FromResult(_h.Leases.Renewals > before), $"heartbeat {beat}");
        }

        // 40 seconds have passed on a 15 second lease: only the heartbeats kept it alive.
        (await Start(rival)).Outcome.ShouldBe(SyncStartOutcome.AlreadyRunning);
        var lease = await _h.Leases.GetAsync("hotels", _ct);
        lease!.ExpiresAt.ShouldBeGreaterThan(_h.Sync.Clock.GetUtcNow());

        release.SetResult();
        await WaitForAsync(owner, SyncState.Completed);
    }

    [Fact]
    public async Task Status_is_readable_from_any_instance()
    {
        _h.WithHotels(300);
        var release = HoldAfterFirstBatch();
        var owner = NewNode();
        var reader = NewNode();
        await owner.StartAsync(_ct);
        var started = await Start(owner);

        await WaitForAsync(reader, SyncState.Running, s => s.Indexed == 100);
        var seen = await reader.Facade.GetStatusAsync("hotels", _ct);

        seen.RunId.ShouldBe(started.RunId);
        seen.LastId.ShouldBe("100");
        release.SetResult();
        await WaitForAsync(reader, SyncState.Completed, s => s.Indexed == 300);
    }

    [Fact]
    public async Task Another_instance_can_cancel_a_run_through_the_shared_flag()
    {
        _h.WithHotels(300);
        _ = HoldAfterFirstBatch();
        var owner = NewNode();
        var other = NewNode();
        await owner.StartAsync(_ct);
        await Start(owner);
        await WaitForAsync(owner, SyncState.Running, s => s.Indexed == 100);
        await HeartbeatTimerExistsAsync();

        (await other.Facade.CancelAsync("hotels", _ct)).ShouldBeTrue();
        Tick(TimeSpan.FromSeconds(5));

        await WaitForAsync(other, SyncState.Cancelled);
        _h.Sync.Checkpoints.Current("hotels").ShouldBe(SyncHarness.Id(100));
        (await _h.Leases.GetAsync("hotels", _ct)).ShouldBeNull("the lease is released once the run has stopped");
    }

    [Fact]
    public async Task A_lease_taken_over_by_someone_else_stops_the_run_and_fails_it()
    {
        _h.WithHotels(300);
        _ = HoldAfterFirstBatch();
        var owner = NewNode();
        await owner.StartAsync(_ct);
        await Start(owner);
        await WaitForAsync(owner, SyncState.Running, s => s.Indexed == 100);
        await HeartbeatTimerExistsAsync();

        _h.Leases.StealLease("hotels", "someone-else", "other-run");
        Tick(TimeSpan.FromSeconds(5));

        await WaitForAsync(owner, SyncState.Failed);
        (await owner.Facade.GetStatusAsync("hotels", _ct)).LastError.ShouldNotBeNull().ShouldContain("lease");
        (await _h.Leases.GetAsync("hotels", _ct))!.OwnerId.ShouldBe("someone-else", "a deposed owner must not release the new owner's lease");
    }

    [Fact]
    public async Task A_deposed_owner_cannot_write_a_checkpoint()
    {
        _h.WithHotels(300);
        var owner = NewNode();
        _h.Sync.Source.BeforeRead = (_, read, _) =>
        {
            if (read == 2)
            {
                _h.Leases.StealLease("hotels", "someone-else", "other-run");
            }

            return Task.CompletedTask;
        };
        await owner.StartAsync(_ct);

        await Start(owner);
        await WaitForAsync(owner, SyncState.Failed);

        _h.Sync.Checkpoints.Saved.ShouldBe([SyncHarness.Id(100)], "the second batch was indexed but its checkpoint was refused");
        (await owner.Facade.GetStatusAsync("hotels", _ct)).LastError.ShouldNotBeNull().ShouldContain("lease");
    }

    [Fact]
    public async Task A_transient_error_while_renewing_does_not_cancel_the_run()
    {
        _h.WithHotels(300);
        var release = HoldAfterFirstBatch();
        var owner = NewNode();
        await owner.StartAsync(_ct);
        await Start(owner);
        await WaitForAsync(owner, SyncState.Running, s => s.Indexed == 100);
        await HeartbeatTimerExistsAsync();

        var failures = 0;
        _h.Leases.FailRenewWhen = () => Interlocked.Increment(ref failures) == 1 ? new TimeoutException("store blip") : null;
        Tick(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(failures >= 1), "the failing heartbeat");
        var renewalsBefore = _h.Leases.Renewals;
        Tick(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(_h.Leases.Renewals > renewalsBefore), "a heartbeat that works again");

        (await owner.Facade.GetStatusAsync("hotels", _ct)).State.ShouldBe(SyncState.Running);
        release.SetResult();
        await WaitForAsync(owner, SyncState.Completed);
    }

    [Fact]
    public async Task A_graceful_shutdown_interrupts_the_run_and_frees_the_collection_at_once()
    {
        _h.WithHotels(300);
        _ = HoldAfterFirstBatch();
        var owner = NewNode();
        var successor = NewNode();
        await owner.StartAsync(_ct);
        await Start(owner);
        await WaitForAsync(owner, SyncState.Running, s => s.Indexed == 100);

        await owner.Host.StopAsync(CancellationToken.None);

        (await successor.Facade.GetStatusAsync("hotels", _ct)).State.ShouldBe(SyncState.Interrupted);
        (await _h.Leases.GetAsync("hotels", _ct)).ShouldBeNull();
        (await Start(successor)).Outcome.ShouldBe(SyncStartOutcome.Started);
    }

    [Fact]
    public async Task A_crashed_owners_collection_is_resumed_from_its_checkpoint_by_another_instance()
    {
        _h.WithHotels(300);
        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _h.Sync.Source.BeforeRead = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                await stall.Task.WaitAsync(token);
            }
        };
        var crashed = NewNode();
        var survivor = NewNode();
        await crashed.StartAsync(_ct);
        await survivor.StartAsync(_ct);
        await Start(crashed);
        await WaitForAsync(crashed, SyncState.Running, s => s.Indexed == 100);

        // The crashed owner is unreachable: its renewals fail and time passes beyond the lease.
        _h.Leases.FailRenewWhen = () => new TimeoutException("owner is gone");
        Tick(TimeSpan.FromSeconds(20));
        await WaitForAsync(survivor, SyncState.Interrupted);
        _h.Leases.FailRenewWhen = null;

        var resumed = await Start(survivor);
        resumed.Outcome.ShouldBe(SyncStartOutcome.Started);
        await WaitForAsync(survivor, SyncState.Completed);

        (await survivor.Facade.GetStatusAsync("hotels", _ct)).Indexed.ShouldBe(200, "the survivor continued after the crashed owners checkpoint at 100");
        _h.Sync.Index.Ids(_h.Sync.IndexName()).Count.ShouldBe(300);
        stall.SetResult();
    }

    [Fact]
    public async Task A_lease_still_held_by_a_run_that_has_already_ended_does_not_block_the_next_start()
    {
        // A run records that it ended before it releases its lease; a caller who saw it end must be able to start again at once.
        var node = NewNode();
        var ended = new RunRecord("ended-run", RunKinds.Sync, "hotels", RunState.Completed, "other-owner", DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, new Dictionary<string, long>(), null, null, false, null);
        await _h.Runs.CreateAsync(ended, _ct);
        await _h.Leases.TryAcquireAsync("hotels", "other-owner", "ended-run", TimeSpan.FromSeconds(15), _ct);

        var started = await Start(node);

        started.Outcome.ShouldBe(SyncStartOutcome.Started);
        (await _h.Leases.GetAsync("hotels", _ct))!.RunId.ShouldBe(started.RunId);
    }

    [Fact]
    public async Task A_lease_held_by_something_that_is_not_a_run_still_blocks_the_start()
    {
        var node = NewNode();
        await _h.Leases.TryAcquireAsync("hotels", "other-owner", "rollback-1234", TimeSpan.FromSeconds(15), _ct);

        var started = await Start(node);

        started.Outcome.ShouldBe(SyncStartOutcome.AlreadyRunning);
        started.RunId.ShouldBe("rollback-1234");
    }
}
