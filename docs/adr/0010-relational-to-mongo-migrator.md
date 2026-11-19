# 10. A resumable relational-to-MongoDB migrator (an extension)

## Status
Accepted. This component was added to round out the migration story around the content store; it is not part of the
core read and sync design.

## Context
Supplier content often starts life in a relational database. Moving it into the per-supplier collections is a one-off
job that is nonetheless big enough to crash, be cancelled, meet dirty rows, and need proof that nothing was lost.

## Decision
- **Keyset batches over a `DbDataReader`.** The configured query is wrapped as a derived table and paged on a configured
  unique key column (`WHERE key IS NOT NULL AND key > @after ORDER BY key LIMIT/TOP n+1`). Each page is read row by row
  through the reader and disposed with its connection, so memory is bounded by one page and no keyset page is ever
  skipped or repeated. The provider (SQLite for tests and the demo, SQL Server through `Microsoft.Data.SqlClient`) is a
  small dialect plus a connection factory behind one `IRowSource` abstraction. SQL Server is covered by dialect and
  connection-type tests only; no test talks to a SQL Server.
- **Definitions are configuration, requests are names.** Source, query, key, column mapping and target collection are
  validated at startup; the API only starts a migration by name, so request bodies never carry SQL.
- **Column mapping with tolerant conversion.** Each column maps to a field with a type (text, integer, double, boolean,
  date, string-or-number). Text that reads as the type is converted ("4.5" to 4.5); anything else, or a missing required
  value, makes the row invalid. A migration does not silently drop fields.
- **Invalid rows are dead-lettered, never fatal.** They go to the existing dead-letter store under the key
  `migration:{name}`, kept apart from sync dead letters, before the batch's checkpoint is written. A row that is fixed in
  the source and migrated later is removed from the dead letters.
- **Idempotent writes and the same checkpoint rules as sync.** Documents are upserted by `_id` (unordered bulk replace),
  the key is checkpointed after the batch is settled, a crash replays at most one batch, a finished run clears its
  checkpoint and a second run changes nothing. The run uses the shared lease and run records, so it is exclusive per
  migration, cancellable from any instance, and visible from any instance.
- **Count verification is reported, not hidden.** At the end the source count (the whole query, including rows with a
  null key, which can never be paged) is compared with the exact target count plus the outstanding dead letters. A
  difference fails the run and puts the numbers in the run's detail.

## Consequences
- The key column must be unique and non-null for the rows to be migrated; rows without a key are surfaced by the count
  check rather than skipped silently, and duplicate keys collapse into one document and surface the same way.
- The target collection counts every document in it, so a collection that already held other documents reports a
  mismatch. Migrate into an empty collection, or read the difference for what it is.
- Configuration carries a connection string, so it belongs in environment variables or user secrets, never in source.
- The upsert and count against a real MongoDB are not exercised by the tests, which use an in-memory target.
