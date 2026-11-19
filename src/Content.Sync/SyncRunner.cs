using System.Security.Cryptography;
using Content.Store.Models;
using Content.Sync.Checkpoints;
using Content.Sync.Configuration;
using Content.Sync.DeadLetters;
using Content.Sync.Search;
using Content.Sync.Source;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Polly;
using Polly.Retry;

namespace Content.Sync;

/// <summary>Counts after a batch, for status reporting.</summary>
public sealed record SyncProgress(string RunId, long Indexed, long DeadLettered, long Batches, string? LastId);

/// <summary>Where a run writes and where it keeps its resume point. The defaults suit a plain sync; a rebuild points them at a new physical index.</summary>
/// <param name="IndexName">Index (or alias) documents are written to.</param>
/// <param name="CheckpointKey">Key of the checkpoint, so a build of a new index never resumes from another run's position.</param>
/// <param name="DeadLetterScope">Groups the dead letters this run writes, so a build can be judged on its own.</param>
public sealed record SyncRunTarget(string IndexName, string CheckpointKey, string DeadLetterScope);

/// <summary>Optional integration points of a run.</summary>
public sealed class SyncRunHooks
{
    /// <summary>Called after each settled batch.</summary>
    public Func<SyncProgress, CancellationToken, Task>? OnProgress { get; init; }

    /// <summary>Called right before every checkpoint write; throws to abort when the run no longer owns its lease.</summary>
    public Func<CancellationToken, Task>? BeforeCheckpoint { get; init; }

    public SyncRunTarget? Target { get; init; }
}

/// <summary>What one finished run did.</summary>
public sealed record SyncRunSummary(
    string RunId,
    string Collection,
    long Indexed,
    long DeadLettered,
    long Batches,
    string? ResumedFromId);

/// <summary>
/// Copies a Mongo collection into its search index with a keyset scan, resumable after any failure.
/// <para>
/// A batch is read (<c>_id &gt; lastId</c>, ascending), decoded, bulk indexed, and only then checkpointed: the
/// checkpoint moves after every document of the batch is indexed or durably dead-lettered, never before. A failure
/// that survives the retry policies aborts the run with the exception surfaced and leaves the checkpoint at the
/// previous batch, so the next run resumes there. Index ids are the source ids, so re-indexing overwrites and a resume
/// can neither lose nor duplicate documents. A completed run deletes its checkpoint; the next one is a full re-scan.
/// </para>
/// </summary>
public sealed class SyncRunner(
    ISourceReader reader,
    OpenSearchBulkIndexer indexer,
    ISearchIndexWriter writer,
    ICheckpointStore checkpoints,
    IDeadLetterStore deadLetters,
    SearchIndexNaming indexNaming,
    TimeProvider timeProvider,
    ILogger<SyncRunner> logger)
{
    public async Task<SyncRunSummary> RunAsync(
        string collection,
        SyncOptions options,
        CancellationToken cancellationToken,
        SyncRunHooks? hooks = null,
        string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(options);

        runId ??= Guid.NewGuid().ToString("N");
        var defaultIndex = indexNaming.IndexFor(collection);
        var target = hooks?.Target ?? new SyncRunTarget(defaultIndex, collection, defaultIndex);
        var indexName = target.IndexName;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["SyncCollection"] = collection, ["SyncRunId"] = runId });

        await RequestRetry
            .Build(options.MaxBulkAttempts, options.BulkBackoffBase, options.BulkBackoffCap, timeProvider, logger, "Creating the search index")
            .ExecuteAsync(async token => await writer.EnsureIndexAsync(indexName, token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        var checkpoint = await checkpoints.GetAsync(target.CheckpointKey, cancellationToken).ConfigureAwait(false);
        var afterId = checkpoint?.LastId;
        var resumedFrom = afterId is null ? null : HotelSearchDocumentMapper.RenderId(afterId);
        logger.LogInformation("Sync of {Collection} into {Index} starting {Mode}.", collection, indexName, resumedFrom is null ? "from the beginning" : $"after id {resumedFrom}");

        var readPipeline = BuildReadPipeline(options);
        long indexed = 0, deadLettered = 0, batches = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = afterId;
            var batch = await readPipeline
                .ExecuteAsync(
                    async token => await reader.ReadBatchAsync(collection, current, options.BatchSize, token).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            if (batch.Documents.Count == 0)
            {
                break;
            }

            var converted = ConvertBatch(batch.Documents);
            var result = await indexer.IndexAsync(indexName, converted.Documents, options, cancellationToken).ConfigureAwait(false);

            var parked = converted.Failures
                .Concat(result.Rejected.Select(r => new DeadLetterEntry(r.Id, r.Reason, converted.HashOf(r.Id))))
                .ToList();

            // Dead letters first, checkpoint second: if the process dies in between, the batch is simply replayed.
            await deadLetters.WriteAsync(collection, parked, target.DeadLetterScope, cancellationToken).ConfigureAwait(false);

            var lastId = batch.Documents[^1]["_id"];
            await BeforeCheckpointAsync(hooks, cancellationToken).ConfigureAwait(false);
            await checkpoints.SaveAsync(target.CheckpointKey, lastId, runId, cancellationToken).ConfigureAwait(false);

            indexed += result.Indexed;
            deadLettered += parked.Count;
            batches++;
            if (hooks?.OnProgress is { } onProgress)
            {
                await onProgress(new SyncProgress(runId, indexed, deadLettered, batches, HotelSearchDocumentMapper.RenderId(lastId)), cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Batch {Batch} done: {Indexed} indexed, {DeadLettered} dead-lettered, checkpoint at {LastId}.",
                batches,
                result.Indexed,
                parked.Count,
                HotelSearchDocumentMapper.RenderId(lastId));

            if (!batch.HasMore)
            {
                break;
            }

            afterId = lastId;
            if (options.InterBatchDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.InterBatchDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        await BeforeCheckpointAsync(hooks, cancellationToken).ConfigureAwait(false);
        await checkpoints.DeleteAsync(target.CheckpointKey, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Sync of {Collection} completed: {Indexed} indexed, {DeadLettered} dead-lettered.", collection, indexed, deadLettered);
        return new SyncRunSummary(runId, collection, indexed, deadLettered, batches, resumedFrom);
    }

    /// <summary>
    /// Runs several collections at once, each with its own cursor, index and checkpoint. All runs finish (or fail)
    /// before this returns; failures are aggregated rather than hidden behind the first.
    /// </summary>
    public async Task<IReadOnlyList<SyncRunSummary>> RunManyAsync(
        IReadOnlyCollection<string> collections,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collections);
        var tasks = collections.Distinct(StringComparer.Ordinal).Select(c => RunAsync(c, options, cancellationToken)).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            var faults = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).ToList();
            throw faults.Count == 0 ? new AggregateException("The runs were cancelled.") : new AggregateException($"{faults.Count} sync run(s) failed.", faults);
        }

        return tasks.Select(t => t.Result).ToList();
    }

    private static Task BeforeCheckpointAsync(SyncRunHooks? hooks, CancellationToken cancellationToken) =>
        hooks?.BeforeCheckpoint is { } before ? before(cancellationToken) : Task.CompletedTask;

    private ConvertedBatch ConvertBatch(IReadOnlyList<BsonDocument> sources)
    {
        var documents = new List<SearchDocument>(sources.Count);
        var bySearchId = new Dictionary<string, BsonDocument>(sources.Count, StringComparer.Ordinal);
        var failures = new List<DeadLetterEntry>();

        foreach (var source in sources)
        {
            try
            {
                var hotel = BsonSerializer.Deserialize<HotelDocument>(source);
                var document = HotelSearchDocumentMapper.Map(hotel);
                documents.Add(document);
                bySearchId[document.Id] = source;
            }
            catch (Exception ex) when (ex is FormatException or BsonSerializationException or InvalidCastException or ArgumentException or OverflowException or KeyNotFoundException)
            {
                var id = HotelSearchDocumentMapper.RenderId(source["_id"]);
                logger.LogWarning(ex, "Document {DocId} cannot be decoded and is being dead-lettered.", id);
                failures.Add(new DeadLetterEntry(id, $"deserialization-failed: {ex.GetType().Name}: {OpenSearchBulkIndexer.Truncate(ex.Message)}", Hash(source)));
            }
        }

        return new ConvertedBatch(documents, bySearchId, failures);
    }

    private ResiliencePipeline<SourceBatch> BuildReadPipeline(SyncOptions options)
    {
        if (options.MaxReadAttempts <= 1)
        {
            return ResiliencePipeline<SourceBatch>.Empty;
        }

        return new ResiliencePipelineBuilder<SourceBatch> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<SourceBatch>
            {
                ShouldHandle = new PredicateBuilder<SourceBatch>()
                    .Handle<MongoConnectionException>()
                    .Handle<MongoExecutionTimeoutException>()
                    .Handle<MongoNotPrimaryException>()
                    .Handle<MongoNodeIsRecoveringException>()
                    .Handle<TimeoutException>()
                    .Handle<IOException>(),
                MaxRetryAttempts = options.MaxReadAttempts - 1,
                Delay = options.ReadBackoffBase,
                MaxDelay = options.ReadBackoffCap,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "Mongo read failed (attempt {Attempt} of {Max}); retrying in {Delay}.",
                        args.AttemptNumber + 1,
                        options.MaxReadAttempts,
                        args.RetryDelay);
                    return default;
                },
            })
            .Build();
    }

    private static string Hash(BsonDocument source) => System.Convert.ToHexStringLower(SHA256.HashData(source.ToBson()));

    private sealed record ConvertedBatch(
        IReadOnlyList<SearchDocument> Documents,
        IReadOnlyDictionary<string, BsonDocument> SourcesBySearchId,
        IReadOnlyList<DeadLetterEntry> Failures)
    {
        public string HashOf(string searchId) =>
            SourcesBySearchId.TryGetValue(searchId, out var source) ? Hash(source) : Hash(new BsonDocument("_id", searchId));
    }
}
