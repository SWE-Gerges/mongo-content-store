# 3. Tolerant serializers: drop the bad field, keep the document

## Status
Accepted

## Context
Supplier data is messy: a rating is `"4.5"`, `4` or `4.5`; images are strings in one document and objects in the next;
an id is a number or a string; an array is truncated or replaced by a sub-document. The stock serializers throw on
such values, and one bad field would make the whole document unreadable, which for a page of results means the whole
page.

## Decision
Seven small serializers (string-or-number, tolerant string list, image url list, tolerant double and nullable double,
scalar object, tolerant string map) plus two generic ones for embedded documents and lists of them read what they
can. Anything they cannot interpret is skipped, reported to an `IMalformedFieldSink` and left at its default; the
document survives. They are bound to document members through explicit class maps, are unit-tested one by one, and
a test decodes a document containing every defect and asserts only the bad fields were dropped. Document classes use
`[BsonIgnoreExtraElements]`.

The driver's serializer registry is process-wide, so registration happens once per process and the first sink wins;
the sink that was bound is the one exposed through dependency injection. It counts defects per field and reason and
publishes them as the `content.mongo.malformed_fields` metric.

## Consequences
- Reads never fail because of a malformed optional field, and the dropped data stays visible as a counter.
- A dropped field is indistinguishable from an absent one in the model (a rating becomes `0`); latitude and longitude
  use a nullable serializer so "unknown" is not confused with the equator.
- Documents that fail for a reason the serializers cannot absorb (for example a non-numeric `_id`) are dead-lettered
  by the sync rather than breaking the run.
