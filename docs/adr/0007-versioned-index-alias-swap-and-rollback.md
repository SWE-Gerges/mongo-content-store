# 7. Versioned search index with a verified, single-request alias swap and rollback

## Status
Accepted

## Context
A plain sync overwrites documents in the index readers use. That is fine for keeping it in step, but a full rebuild
(a mapping change, a fixed serializer, a suspect index) done in place leaves readers on a half-built index, and there
is no way back if the result is wrong. Rebuilding into a fresh index and switching readers over is the usual answer;
the switch has to be atomic and has to be judged before it happens.

## Decision
- **Physical indexes are `{alias}-v{N}`**; readers and the plain sync use the alias name. A rebuild takes the next
  free number, fills it with the same sync loop (own checkpoint key `rebuild:{index}`, own dead-letter scope), and the
  alias keeps pointing at the old version while it builds.
- **Verify before swapping.** The count must be within a configured fraction of what the source minus this build's dead
  letters should have produced, the build must not have dead-lettered more than a threshold, and a random sample of
  source documents must exist in the new index with the same field values. A sampled document may be missing only as far
  as dead letters explain it. A build that fails is marked `Failed` and **kept for inspection**; the alias does not move
  and the run is recorded as failed with the reasons.
- **One `_aliases` request** removes the old target and adds the new one in the same action list, so a reader never sees
  the alias missing. When the alias name is still a plain index (created by an earlier unversioned sync), the same request
  also deletes that index (`remove_index`), the only way to give an index's name to an alias without a gap.
- **Which versions may be rolled back to is remembered**, in `index_versions` (Building, Live, Retired, Failed). A failed
  build is never a rollback target. The alias target on the cluster is the authority: whenever the store and the cluster
  disagree (a crash between the swap and the bookkeeping) the store is corrected from the alias.
- **Retention.** After a successful swap the newest K retired versions are kept (default 2) and older ones are deleted,
  along with failed builds, which have served their purpose by then.
- **Rollback** points the alias at the most recently retired version that still exists (or a named one) with the same
  single request, forgets retained versions that were deleted behind our back, and is refused when none is left.
- **Rebuilds and rollbacks use the collection's run lease**, so they exclude each other and a plain sync of the same
  collection. A rebuild that was interrupted resumes into the same index from its checkpoint instead of starting a new one.

## Consequences
- Readers are never left without an index, and a bad build costs disk space, not correctness.
- Atomicity of the alias change is a property of the cluster's `_aliases` API. The tests prove that the code sends exactly
  one request containing both actions; they run on an in-memory cluster, so the cluster-side guarantee itself is not
  exercised here.
- A rebuild needs room for a second copy of the index while it builds and for K retained copies afterwards.
- A verification threshold that is too tight blocks a legitimate rebuild; the failed build stays visible with the failing
  check named, and the thresholds are configuration.
