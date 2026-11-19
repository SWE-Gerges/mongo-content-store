# 2. Look hotels up with a numeric `$in` on `_id`

## Status
Accepted

## Context
Callers hand over ids as text (route and query parameters). The tempting implementation converts the stored side
to text: `ids.Contains(hotel.Id.ToString())`. The MongoDB LINQ provider turns that into an aggregation expression
(`$expr` with `$toString`) that is evaluated per document.

## Decision
Parse the ids to `long` at the edge and query `Filter.In(h => h.Id, longs)`, which is `{ _id: { $in: [Int64 ...] } }`.
Never compare a converted `_id`. The unit tests render the filter and assert it contains only `Int64` values and no
`$expr`. That the server then answers it from the `_id` index is not asserted by the test suite (it needs a real
MongoDB); `bench/Content.Bench` runs `explain` and timings for both shapes on seeded data (see `bench/RESULTS.md`).

## Consequences
- The lookup uses the `_id` index and its cost grows with the number of ids asked for, not with the collection size.
- Ids that are not numbers are rejected as a 400 before any query runs.
- Suppliers that key hotels by an alphanumeric code use `GetByCodes`, a plain `$in` on an indexed string field.
