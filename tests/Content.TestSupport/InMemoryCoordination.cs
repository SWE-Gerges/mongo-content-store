using Content.Mongo.Coordination;
using MongoDB.Bson;

namespace Content.TestSupport;

/// <summary>Lease store with the same semantics as the MongoDB one, driven by an injectable clock.</summary>
public sealed class InMemoryLeaseStore(TimeProvider clock) : ILeaseStore
{
    private readonly Dictionary<string, LeaseInfo> _leases = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Returns an exception to throw from <see cref="RenewAsync"/>, to simulate a transient store failure.</summary>
    public Func<Exception?>? FailRenewWhen { get; set; }

    public int Renewals { get; private set; }

    public Task<Lease?> TryAcquireAsync(string key, string ownerId, string runId, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_leases.TryGetValue(key, out var held) && held.ExpiresAt >= now)
            {
                return Task.FromResult<Lease?>(null);
            }

            _leases[key] = new LeaseInfo(key, ownerId, runId, now, now + duration);
            return Task.FromResult<Lease?>(new Lease(key, ownerId, runId));
        }
    }

    public Task<bool> RenewAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (FailRenewWhen?.Invoke() is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        lock (_gate)
        {
            if (!_leases.TryGetValue(lease.Key, out var held) || held.OwnerId != lease.OwnerId || held.RunId != lease.RunId)
            {
                return Task.FromResult(false);
            }

            var now = clock.GetUtcNow();
            _leases[lease.Key] = held with { HeartbeatAt = now, ExpiresAt = now + duration };
            Renewals++;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(Lease lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(lease.Key, out var held) && held.OwnerId == lease.OwnerId && held.RunId == lease.RunId)
            {
                _leases.Remove(lease.Key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<LeaseInfo?> GetAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_leases.GetValueOrDefault(key));
        }
    }

    /// <summary>Test helper: hands the lease to someone else, as if it had expired and been taken over.</summary>
    public void StealLease(string key, string ownerId, string runId)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            _leases[key] = new LeaseInfo(key, ownerId, runId, now, now.AddHours(1));
        }
    }
}

public sealed class InMemoryRunStore : IRunStore
{
    private readonly List<RunRecord> _runs = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RunRecord> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public Task CreateAsync(RunRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _runs.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task MarkRunningAsync(string runId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Update(runId, r => r with { State = RunState.Running, StartedAt = at });

    public Task UpdateProgressAsync(string runId, IReadOnlyDictionary<string, long> counters, string? lastId, CancellationToken cancellationToken) =>
        Update(runId, r => r with { Counters = new Dictionary<string, long>(counters), LastId = lastId ?? r.LastId });

    public Task FinishAsync(string runId, RunState state, string? error, BsonDocument? detail, DateTimeOffset at, CancellationToken cancellationToken) =>
        Update(runId, r => r with { State = state, LastError = error, Detail = detail ?? r.Detail, FinishedAt = at });

    public Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _runs.FindIndex(r => r.Id == runId);
            if (index < 0 || !_runs[index].IsActive)
            {
                return Task.FromResult(false);
            }

            _runs[index] = _runs[index] with { CancelRequested = true };
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.Any(r => r.Id == runId && r.CancelRequested));
        }
    }

    public Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.FirstOrDefault(r => r.Id == runId));
        }
    }

    public Task<RunRecord?> GetLatestAsync(string target, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.LastOrDefault(r => r.Target == target));
        }
    }

    public Task MarkInterruptedAsync(string target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                if (_runs[i].Target == target && _runs[i].IsActive)
                {
                    _runs[i] = _runs[i] with { State = RunState.Interrupted, FinishedAt = at };
                }
            }
        }

        return Task.CompletedTask;
    }

    private Task Update(string runId, Func<RunRecord, RunRecord> change)
    {
        lock (_gate)
        {
            var index = _runs.FindIndex(r => r.Id == runId);
            if (index >= 0)
            {
                _runs[index] = change(_runs[index]);
            }
        }

        return Task.CompletedTask;
    }
}
