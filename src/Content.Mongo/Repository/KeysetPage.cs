using MongoDB.Bson;

namespace Content.Mongo.Repository;

/// <summary>
/// One page of a keyset scan. The repository fetches <c>limit + 1</c> documents, so
/// <see cref="HasMore"/> is known without a count query.
/// </summary>
/// <param name="Items">At most <c>limit</c> documents in ascending <c>_id</c> order.</param>
/// <param name="HasMore">True when at least one more document follows <paramref name="LastId"/>.</param>
/// <param name="LastId">The <c>_id</c> of the last item, usable as the next <c>afterId</c>; null for an empty page.</param>
public sealed record KeysetPage<T>(IReadOnlyList<T> Items, bool HasMore, BsonValue? LastId);
