# 5. Flat `ProviderKeys` instead of a `nested` mapping

## Status
Accepted

## Context
A merged hotel lists the supplier ids it was built from: `Providers: [{ Supplier, HotelId }, ...]`. The search question
is "which hotel has supplier X with id Y". OpenSearch flattens an array of objects, so a bool query that asks for
`providers.supplier = X` and `providers.hotelId = Y` can match two different entries (X with some other id, and some
other supplier with Y). The usual fix is a `nested` mapping with nested queries.

## Decision
Search documents carry a flat keyword array `ProviderKeys` of `"{supplier}:{hotelId}"` strings. The question becomes a
single term query on one field. In Mongo the same question is answered with `$elemMatch` over `Providers[]`, which does
match within one array element, using `$in` over the string and numeric forms of the id.

## Consequences
- No `nested` mapping, no nested queries, no join-style cost, and the field is cheap to filter and aggregate on.
- The key format is a contract: a supplier key or hotel id containing `:` would be ambiguous. Supplier keys are
  restricted to lowercase letters, digits and hyphens, which keeps the first `:` unambiguous.
- Partial matches ("any hotel of supplier X") need a prefix or wildcard query on the keyword field.
