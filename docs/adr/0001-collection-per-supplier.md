# 1. One collection per supplier, plus a merged master

## Status
Accepted

## Context
Several suppliers send hotel content in overlapping but different shapes. Ids are only unique within a supplier,
volumes differ by orders of magnitude, and each supplier is refreshed on its own schedule. The alternative is one
polymorphic `hotels` collection with a `supplier` discriminator on every document.

## Decision
Each supplier has its own collection named by a strategy (`hotel_{supplierKey}`, for example `hotel_supplier-a`),
and a separate `hotels` collection holds the merged master. Supplier keys are plain configured strings: adding a
supplier is configuration, not code. A key is validated (lowercase letters, digits, single hyphens) because it ends
up inside collection and index names. The numeric supplier id is the document `_id`.

## Consequences
- Lookups by supplier id are a primary-key read on `_id`; no compound `(supplier, id)` index is needed.
- A bulk reload or a sync of one supplier touches only that collection, and a collection can be dropped, rebuilt or
  indexed independently.
- Indexes are declared once and ensured on every supplier collection by the index bootstrapper.
- Cross-supplier queries need either the merged master or one query per collection; the master carries
  `Providers[]` back-references for that.
- Many collections means many index sets; this is acceptable for a handful of suppliers and would be revisited at
  hundreds.
