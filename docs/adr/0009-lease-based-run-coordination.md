# 9. Persisted runs with a per-target lease

## Status
Accepted

## Context
Run state kept in one process cannot be read from a second API instance, and nothing stops two instances from syncing
the same collection at once, which doubles the load and makes two checkpoints race. A crashed process must not block a
collection forever either.

## Decision
- **Runs are records** in `sync_runs` (state, counters, last checkpointed key, error, cancel flag, a kind-specific
  detail), so any instance can report on or cancel any run.
- **A lease per target** in `sync_leases`, taken with one `findOneAndUpdate` upsert whose filter only matches an expired
  lease. An absent lease is inserted, an expired one is taken over, a live one collides on `_id` (duplicate key), which is
  how losing the race is detected. Exactly one instance wins; the loser is told which run holds it.
- **Heartbeat.** The owner renews the lease on an interval (at most half the lease duration, so one missed beat is
  survivable) and looks for a cancel request; a transient error while renewing is logged and retried, a lost lease
  cancels the run. Before every checkpoint write the run re-checks that it still owns the lease, so a deposed owner can
  never write one.
- **Crash hand-over.** A crashed owner stops heartbeating, its lease expires, and the next start takes it over, marks the
  orphaned run interrupted and resumes from the checkpoint. A status read treats an active run whose lease has lapsed as
  interrupted without waiting for anyone to take over.
- **An ended run does not hold its target.** A run records that it ended before it releases its lease, so for an instant
  the lease of a finished run is still there. Starting a run (or taking the lease for a rollback) treats a lease whose run
  has ended as released, so a caller who just saw a run finish can start the next one at once and never meets a spurious
  conflict. A holder with no run record (a rollback) or an active run still blocks.
- **Kinds.** Sync, index rebuild and migration runs share this machinery; a worker per kind executes them.

## Consequences
- Instances need clocks that agree well within the lease duration (default 30 s); the lease compares times written by
  different machines.
- A graceful shutdown marks its runs interrupted and frees the target at once; a crash costs up to one lease duration.
- The lease store, run store and workers are ports with in-memory implementations, so the race, the expiry, the
  heartbeat and the hand-over are tested deterministically with a fake clock. The `findOneAndUpdate` itself is not
  exercised without a real server.
