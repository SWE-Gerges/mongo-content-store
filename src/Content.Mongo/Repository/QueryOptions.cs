using MongoDB.Driver;

namespace Content.Mongo.Repository;

/// <summary>Optional shaping applied to a filtered read.</summary>
public sealed class QueryOptions<T>
{
    public ProjectionDefinition<T>? Projection { get; init; }

    public SortDefinition<T>? Sort { get; init; }

    public int? Limit { get; init; }

    public Collation? Collation { get; init; }

    public TimeSpan? MaxTime { get; init; }
}
