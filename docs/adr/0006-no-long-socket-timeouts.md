# 6. Bound connecting and server selection, leave the socket timeout unset

## Status
Accepted

## Context
The Mongo driver can time out a socket read after a configurable period. A long value (minutes) looks harmless but it
hides outages: a stalled server keeps requests hanging for that whole period, and connection pools fill with stuck
operations. A short value kills legitimately long operations such as a large scan or a bulk write.

## Decision
`MongoOptions` exposes the connect timeout (5 s), the server-selection timeout (5 s) and the maximum pool size (100), and
deliberately no socket timeout. The driver default (none) is kept, and the options validator rejects a connection string
that sets `socketTimeoutMS`. Operations that must be bounded say so themselves: counts accept a maximum time, name
searches run with a server-side time limit, and request cancellation is honoured everywhere through the cancellation
token.

## Consequences
- When MongoDB is unreachable callers fail within seconds (server selection), not minutes.
- Long operations are bounded by intent, per operation, instead of by one global number.
- A server that accepts a connection and then stalls is caught by the caller's cancellation token or an operation time
  limit rather than by a socket timer.
