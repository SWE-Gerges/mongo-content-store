using MongoDB.Bson;
using MongoDB.Driver;

namespace Content.Mongo.Coordination;

public enum RunState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>The owner went away (crash, shutdown, lost lease) before the run finished; the checkpoint allows a resume.</summary>
    Interrupted,
}

/// <summary>One persisted run of any kind (sync, index rebuild, migration).</summary>
/// <param name="Target">What the run works on and the lease key: a collection name, or <c>migration:{name}</c>.</param>
/// <param name="Counters">Progress counters such as indexed, deadLettered, batches.</param>
/// <param name="LastId">Last checkpointed key, rendered as text.</param>
/// <param name="Detail">Kind-specific result written when the run finishes (for example the verification outcome).</param>
public sealed record RunRecord(
    string Id,
    string Kind,
    string Target,
    RunState State,
    string OwnerId,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyDictionary<string, long> Counters,
    string? LastId,
    string? LastError,
    bool CancelRequested,
    BsonDocument? Detail)
{
    public bool IsActive => State is RunState.Queued or RunState.Running;
}

public interface IRunStore
{
    Task CreateAsync(RunRecord record, CancellationToken cancellationToken);

    Task MarkRunningAsync(string runId, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateProgressAsync(string runId, IReadOnlyDictionary<string, long> counters, string? lastId, CancellationToken cancellationToken);

    Task FinishAsync(string runId, RunState state, string? error, BsonDocument? detail, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>True when the run exists and is still active.</returns>
    Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken);

    Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken);

    Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken);

    /// <summary>The most recently requested run for the target, whatever its kind.</summary>
    Task<RunRecord?> GetLatestAsync(string target, CancellationToken cancellationToken);

    /// <summary>Marks every still-active run of the target as interrupted. Called by whoever has just taken the target's lease.</summary>
    Task MarkInterruptedAsync(string target, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Run records in the <c>sync_runs</c> collection.</summary>
internal sealed class MongoRunStore(IMongoDatabaseFactory databases) : IRunStore
{
    public const string CollectionName = "sync_runs";

    private static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    private static UpdateDefinitionBuilder<BsonDocument> Update => Builders<BsonDocument>.Update;

    private static readonly string[] ActiveStates = [nameof(RunState.Queued), nameof(RunState.Running)];

    public async Task CreateAsync(RunRecord record, CancellationToken cancellationToken) =>
        await Source().InsertOneAsync(ToDocument(record), cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task MarkRunningAsync(string runId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await Source()
            .UpdateOneAsync(Filter.Eq("_id", runId), Update.Set("state", nameof(RunState.Running)).Set("startedAt", at.UtcDateTime), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task UpdateProgressAsync(string runId, IReadOnlyDictionary<string, long> counters, string? lastId, CancellationToken cancellationToken)
    {
        var update = Update.Set("counters", CountersToBson(counters));
        if (lastId is not null)
        {
            update = update.Set("lastId", lastId);
        }

        await Source().UpdateOneAsync(Filter.Eq("_id", runId), update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task FinishAsync(string runId, RunState state, string? error, BsonDocument? detail, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var update = Update.Set("state", state.ToString()).Set("finishedAt", at.UtcDateTime).Set("lastError", error is null ? (BsonValue)BsonNull.Value : error);
        if (detail is not null)
        {
            update = update.Set("detail", detail);
        }

        await Source().UpdateOneAsync(Filter.Eq("_id", runId), update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestCancelAsync(string runId, CancellationToken cancellationToken)
    {
        var result = await Source()
            .UpdateOneAsync(
                Filter.And(Filter.Eq("_id", runId), Filter.In("state", ActiveStates)),
                Update.Set("cancelRequested", true),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    public async Task<bool> IsCancelRequestedAsync(string runId, CancellationToken cancellationToken)
    {
        var stored = await Source()
            .Find(Filter.Eq("_id", runId))
            .Project(Builders<BsonDocument>.Projection.Include("cancelRequested"))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return stored is not null && stored.GetValue("cancelRequested", false).ToBoolean();
    }

    public async Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken)
    {
        var stored = await Source().Find(Filter.Eq("_id", runId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return stored is null ? null : FromDocument(stored);
    }

    public async Task<RunRecord?> GetLatestAsync(string target, CancellationToken cancellationToken)
    {
        var stored = await Source()
            .Find(Filter.Eq("target", target))
            .Sort(Builders<BsonDocument>.Sort.Descending("requestedAt"))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return stored is null ? null : FromDocument(stored);
    }

    public async Task MarkInterruptedAsync(string target, DateTimeOffset at, CancellationToken cancellationToken) =>
        await Source()
            .UpdateManyAsync(
                Filter.And(Filter.Eq("target", target), Filter.In("state", ActiveStates)),
                Update.Set("state", nameof(RunState.Interrupted)).Set("finishedAt", at.UtcDateTime),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private static BsonDocument ToDocument(RunRecord record) => new()
    {
        { "_id", record.Id },
        { "kind", record.Kind },
        { "target", record.Target },
        { "state", record.State.ToString() },
        { "ownerId", record.OwnerId },
        { "requestedAt", record.RequestedAt.UtcDateTime },
        { "startedAt", record.StartedAt is { } started ? started.UtcDateTime : BsonNull.Value },
        { "finishedAt", record.FinishedAt is { } finished ? finished.UtcDateTime : BsonNull.Value },
        { "counters", CountersToBson(record.Counters) },
        { "lastId", record.LastId is null ? BsonNull.Value : record.LastId },
        { "lastError", record.LastError is null ? BsonNull.Value : record.LastError },
        { "cancelRequested", record.CancelRequested },
        { "detail", record.Detail is null ? BsonNull.Value : record.Detail },
    };

    private static RunRecord FromDocument(BsonDocument document) => new(
        document["_id"].AsString,
        document["kind"].AsString,
        document["target"].AsString,
        Enum.Parse<RunState>(document["state"].AsString),
        document["ownerId"].AsString,
        ToTime(document["requestedAt"]),
        document["startedAt"].IsBsonNull ? null : ToTime(document["startedAt"]),
        document["finishedAt"].IsBsonNull ? null : ToTime(document["finishedAt"]),
        document["counters"].AsBsonDocument.ToDictionary(e => e.Name, e => e.Value.ToInt64()),
        document["lastId"].IsBsonNull ? null : document["lastId"].AsString,
        document["lastError"].IsBsonNull ? null : document["lastError"].AsString,
        document["cancelRequested"].ToBoolean(),
        document["detail"].IsBsonNull ? null : document["detail"].AsBsonDocument);

    private static BsonDocument CountersToBson(IReadOnlyDictionary<string, long> counters)
    {
        var document = new BsonDocument();
        foreach (var (name, value) in counters)
        {
            document[name] = value;
        }

        return document;
    }

    private static DateTimeOffset ToTime(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);

    private IMongoCollection<BsonDocument> Source() => databases.Get().GetCollection<BsonDocument>(CollectionName);
}

/// <summary>The kinds of run the coordination layer knows how to host.</summary>
public static class RunKinds
{
    public const string Sync = "sync";
    public const string Rebuild = "rebuild";
    public const string Migration = "migration";
}
