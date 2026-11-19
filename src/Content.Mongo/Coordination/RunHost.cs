using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Content.Mongo.Coordination;

/// <summary>
/// Executes the runs this instance has taken the lease for. Runs on different targets proceed in parallel. While a
/// run executes, an independent heartbeat renews its lease and watches for a cancel request; if the lease is
/// definitely lost the run is cancelled, while a transient renewal error is only logged and retried.
/// </summary>
internal sealed class RunHost(
    RunCoordinator coordinator,
    IEnumerable<IRunWorker> workers,
    ILogger<RunHost> logger) : BackgroundService
{
    private readonly Dictionary<string, IRunWorker> _workers = workers.ToDictionary(w => w.Kind, StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new List<Task>();
        try
        {
            await foreach (var job in coordinator.Jobs.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                active.RemoveAll(task => task.IsCompleted);
                active.Add(ExecuteJobAsync(job, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Run host stopping; {Count} run(s) are being interrupted.", active.Count(t => !t.IsCompleted));
        }

        await Task.WhenAll(active).ConfigureAwait(false);
        await ReleaseUnstartedAsync().ConfigureAwait(false);
    }

    private async Task ExecuteJobAsync(RunJob job, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        coordinator.RegisterLocal(job.RunId, cancellation);
        var leaseLost = false;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = Task.CompletedTask;

        try
        {
            if (!_workers.TryGetValue(job.Kind, out var worker))
            {
                throw new InvalidOperationException($"No worker is registered for run kind '{job.Kind}'.");
            }

            await coordinator.Runs.MarkRunningAsync(job.RunId, coordinator.Clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            heartbeat = HeartbeatAsync(job, cancellation, () => leaseLost = true, heartbeatStop.Token);

            var context = new RunContext(
                job.RunId,
                job.Kind,
                job.Target,
                job.Parameters,
                (counters, lastId, token) => coordinator.Runs.UpdateProgressAsync(job.RunId, counters, lastId, token),
                async token =>
                {
                    if (!await coordinator.Leases.RenewAsync(job.Lease, coordinator.Options.LeaseDuration, token).ConfigureAwait(false))
                    {
                        throw new LeaseLostException(job.Target);
                    }
                });

            var detail = await worker.ExecuteAsync(context, cancellation.Token).ConfigureAwait(false);
            await FinishAsync(job, RunState.Completed, null, detail).ConfigureAwait(false);
        }
        catch (LeaseLostException ex)
        {
            logger.LogError(ex, "Run {RunId} lost its lease and stopped.", job.RunId);
            await FinishAsync(job, RunState.Failed, ex.Message, null).ConfigureAwait(false);
        }
        catch (RunFailedException ex)
        {
            logger.LogWarning("Run {RunId} on {Target} failed: {Reason}", job.RunId, job.Target, ex.Message);
            await FinishAsync(job, RunState.Failed, ex.Message, ex.Detail).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (leaseLost)
            {
                await FinishAsync(job, RunState.Failed, new LeaseLostException(job.Target).Message, null).ConfigureAwait(false);
            }
            else if (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Run {RunId} on {Target} was interrupted by shutdown; its checkpoint allows a resume.", job.RunId, job.Target);
                await FinishAsync(job, RunState.Interrupted, null, null).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("Run {RunId} on {Target} was cancelled; its checkpoint stays at the last completed batch.", job.RunId, job.Target);
                await FinishAsync(job, RunState.Cancelled, null, null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} on {Target} failed; its checkpoint stays at the last completed batch.", job.RunId, job.Target);
            await FinishAsync(job, RunState.Failed, $"{ex.GetType().Name}: {ex.Message}", null).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            coordinator.UnregisterLocal(job.RunId);
            await ReleaseAsync(job.Lease).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(RunJob job, CancellationTokenSource run, Action onLeaseLost, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(coordinator.Options.HeartbeatInterval, coordinator.Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    var renewed = await coordinator.Leases.RenewAsync(job.Lease, coordinator.Options.LeaseDuration, stop).ConfigureAwait(false);
                    if (!renewed)
                    {
                        onLeaseLost();
                        await run.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    if (await coordinator.Runs.IsCancelRequestedAsync(job.RunId, stop).ConfigureAwait(false))
                    {
                        await run.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Heartbeat for run {RunId} failed; it will be retried.", job.RunId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run finished and stopped the heartbeat.
        }
    }

    private async Task FinishAsync(RunJob job, RunState state, string? error, BsonDocument? detail)
    {
        try
        {
            await coordinator.Runs.FinishAsync(job.RunId, state, error, detail, coordinator.Clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record that run {RunId} ended as {State}.", job.RunId, state);
        }
    }

    private async Task ReleaseAsync(Lease lease)
    {
        try
        {
            await coordinator.Leases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not release the lease on {Key}; it will expire on its own.", lease.Key);
        }
    }

    private async Task ReleaseUnstartedAsync()
    {
        while (coordinator.Jobs.TryRead(out var job))
        {
            await FinishAsync(job, RunState.Interrupted, null, null).ConfigureAwait(false);
            await ReleaseAsync(job.Lease).ConfigureAwait(false);
        }
    }
}
