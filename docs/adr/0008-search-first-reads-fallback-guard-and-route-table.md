# 8. Search-index-first reads, the fallback guard and a validated route table

## Status
Accepted

## Context
Lookups by id are the hot path. The search index can answer them without loading MongoDB, but the index can be behind
(a sync has not reached a document yet), slow, or down. A read path that trusts the index blindly serves holes; one that
reacts to every "not found" amplifies an outage into a stampede of resyncs.

## Decision
- **Index first, MongoDB completes.** A lookup by ids (or codes) asks the supplier's index alias; whatever the index does
  not return is read from MongoDB (partial-hit completion) and merged. The response header `X-Content-Source` says
  `index`, `mongo` or `mixed`. The index stores rooms and provider ids in its source (not indexed), so the response is the
  same whichever side served it; a test serves one hotel both ways and compares the bodies.
- **Only an answering index can say something is missing.** If the index is unreachable, refuses the request, or does not
  answer within a configured timeout, the whole read is served from MongoDB and nothing is recorded as missing: an outage
  is no statement about the documents. Only when the index answered, and MongoDB then has documents it lacked, are those
  keys reported (a `content.reads.index_misses` counter and a sink an operator can attach a resync to). Keys that exist
  nowhere are not a signal. A caller that disconnects cancels the read; it is not mistaken for a slow index.
- **Bounded fallback.** The MongoDB query carries a server-side `MaxTime` and the caller's cancellation token. If it fails
  or times out the request fails (503); a half-answered read is never returned silently, and nothing retries forever.
- **An explicit route table** maps every supplier to its collection and index alias. It is validated when the host starts:
  every configured supplier needs a route, no supplier, collection or alias may repeat, no route may dangle, and each
  route must name the collection the content store actually reads and the alias sync actually serves. Any failure stops
  the host and lists every problem. There is no default route; an unknown supplier at runtime is a 404 problem.
- Paging a city, name search, provider lookup and room detail stay on MongoDB (they need the full document or an index
  the search mapping does not provide), and say so with `X-Content-Source: mongo`.

## Consequences
- A stale or missing index degrades to slower reads, not wrong ones.
- The index source is larger by the rooms and provider lists. Coordinates are indexed as a pair, so a hotel with only one
  of latitude or longitude comes back from the index without either.
- The route table repeats information the naming strategy already implies. That is the point: a configuration drift
  between what is read, what is synced and what is routed fails at startup instead of at the first request.
