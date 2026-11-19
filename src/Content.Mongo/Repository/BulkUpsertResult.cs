namespace Content.Mongo.Repository;

public sealed record BulkUpsertResult(long Matched, long Modified, long Upserted)
{
    public static BulkUpsertResult Empty { get; } = new(0, 0, 0);
}
