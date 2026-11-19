# 4. Keyset checkpointing, dead letters and idempotent upserts for the search sync

## Status
Accepted

## Context
Copying millions of documents into a search index takes long enough that crashes, restarts and cluster hiccups are
normal. The copy must not lose documents, must not duplicate them, must not stop for one bad document, and must not
hide a failure behind a success.

## Decision
- **Keyset scan.** A run reads `_id > lastId` in ascending order, `batchSize` at a time (fetching one extra row to know
  whether more follow), so progress is a single value and no scan restarts from the beginning.
- **Checkpoint after the batch is settled.** The checkpoint is written only after every document of the batch is either
  indexed or durably written to the dead-letter collection. A crash in between replays that batch.
- **Idempotent writes.** The search document id is the source `_id` rendered as an invariant string: integers as digits,
  never through a floating-point value, so two different ids cannot collapse into one document. Re-indexing a batch
  overwrites.
- **Bulk rejections are inspected.** A bulk call that returns 200 can still reject items. Retryable rejections
  (429/503) get one retry after a pause; permanent ones (mapping conflicts, invalid values) go to `_sync_deadletter`
  with a reason and a payload hash before the checkpoint advances.
- **A retryable rejection that survives its retry aborts the run; it is not dead-lettered.** Dead letters mean "this
  document can never be indexed" (a bad coordinate, a value the mapping refuses). A 429 or 503 that persists after a
  retry says the cluster is overloaded, not that the data is bad. Parking those documents would silently hollow out the
  index while the run reports success, and the dead-letter list would fill with healthy documents that nobody should
  hand-fix. So the run stops with the exception surfaced (`BulkRetriesExhaustedException`), the checkpoint stays at the
  previous batch, the run is recorded as failed with that error, and the lease is released.
- **What an operator does next.** Read the failed run's `lastError` and `lastId` from the status route, fix or wait out
  the cluster problem (disk watermark, queue capacity, a node down), then start the sync again: it resumes after the
  checkpoint and replays at most the one batch that failed. Nothing needs cleaning up. If the same documents are
  rejected permanently, the cluster eventually answers with a non-retryable status for them and they are dead-lettered
  on the next attempt, which is the right place for them.
- **Other infrastructure failures abort too.** A request-level outage is retried with backoff (bounded, with jitter,
  cancellable) and then raised. Creating the index is retried the same way before the first read.
- **Bounded reads.** Mongo read failures are retried with exponential backoff capped at 60 seconds and a maximum number
  of attempts, always cancellable. A document that cannot be decoded is dead-lettered by its raw `_id` and the run
  continues.
- **Dead letters are listed with keyset paging** (newest first, ties broken by key; the cursor carries the timestamp at
  millisecond resolution, which is what MongoDB stores), so a listing over millions of rows stays cheap.
- A completed run deletes its checkpoint; the next run is a full, idempotent re-scan.

## Consequences
- A resume loses nothing and duplicates nothing; tests assert the index holds exactly the source set after a crash.
- At most one batch of work is repeated after a crash.
- Dead letters accumulate until someone looks; they are listed through the API and re-attempts bump `attempts`.
- Run state and the per-collection lease live in MongoDB (`sync_runs`, `sync_leases`), so any instance can report on a
  run and a crashed owner's collection is free again once its lease expires.
