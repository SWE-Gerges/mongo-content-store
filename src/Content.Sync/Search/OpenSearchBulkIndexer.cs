using System.Runtime.ExceptionServices;
using Content.Sync.Configuration;
using Microsoft.Extensions.Logging;
using Polly;

namespace Content.Sync.Search;

/// <summary>A document that could not be indexed and will not be by retrying.</summary>
public sealed record RejectedDocument(string Id, string Reason);

public sealed record BulkIndexResult(int Indexed, IReadOnlyList<RejectedDocument> Rejected);

/// <summary>
/// Splits a batch into bulk requests (500 by default, two in flight), retries what is worth retrying and reports
/// what is not. A bulk call that returns 200 can still reject items, so every item is inspected:
/// retryable rejections (429/503) get one more attempt after a pause; permanent rejections are returned for
/// dead-lettering; a request-level outage is retried with backoff and then thrown. Failures in parallel chunks
/// are never swallowed: one is rethrown as is, several are aggregated.
/// </summary>
public sealed class OpenSearchBulkIndexer(ISearchIndexWriter writer, TimeProvider timeProvider, ILogger<OpenSearchBulkIndexer> logger)
{
    public async Task<BulkIndexResult> IndexAsync(
        string indexName,
        IReadOnlyList<SearchDocument> documents,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        if (documents.Count == 0)
        {
            return new BulkIndexResult(0, []);
        }

        var pipeline = BuildRequestPipeline(options);
        var chunks = documents.Chunk(options.BulkChunkSize).Select(chunk => (IReadOnlyList<SearchDocument>)chunk).ToList();

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(options.BulkMaxParallelism);
        var tasks = chunks.Select(chunk => IndexChunkAsync(indexName, chunk, options, pipeline, gate, abort)).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Task.WhenAll rethrows only the first failure; look at every chunk so none is lost behind it.
            cancellationToken.ThrowIfCancellationRequested();
            var faults = tasks
                .Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception!.InnerExceptions)
                .ToList();
            if (faults.Count == 1)
            {
                ExceptionDispatchInfo.Capture(faults[0]).Throw();
            }

            if (faults.Count > 1)
            {
                throw new AggregateException($"{faults.Count} bulk chunks failed.", faults);
            }

            throw;
        }

        var results = tasks.Select(task => task.Result).ToList();
        return new BulkIndexResult(results.Sum(r => r.Indexed), results.SelectMany(r => r.Rejected).ToList());
    }

    private async Task<BulkIndexResult> IndexChunkAsync(
        string indexName,
        IReadOnlyList<SearchDocument> chunk,
        SyncOptions options,
        ResiliencePipeline pipeline,
        SemaphoreSlim gate,
        CancellationTokenSource abort)
    {
        await gate.WaitAsync(abort.Token).ConfigureAwait(false);
        try
        {
            return await IndexChunkCoreAsync(indexName, chunk, options, pipeline, abort.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await abort.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BulkIndexResult> IndexChunkCoreAsync(
        string indexName,
        IReadOnlyList<SearchDocument> chunk,
        SyncOptions options,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken)
    {
        var first = await SendAsync(indexName, chunk, pipeline, cancellationToken).ConfigureAwait(false);
        var indexed = first.Indexed;
        var rejected = first.Failures.Where(f => !f.Retryable).Select(ToRejected).ToList();

        var retryable = first.Failures.Where(f => f.Retryable).ToList();
        if (retryable.Count == 0)
        {
            return new BulkIndexResult(indexed, rejected);
        }

        logger.LogWarning("Retrying {Count} document(s) the cluster rejected as retryable.", retryable.Count);
        await Task.Delay(options.ItemRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);

        var ids = retryable.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var again = chunk.Where(document => ids.Contains(document.Id)).ToList();
        var second = await SendAsync(indexName, again, pipeline, cancellationToken).ConfigureAwait(false);
        indexed += second.Indexed;
        rejected.AddRange(second.Failures.Where(f => !f.Retryable).Select(ToRejected));

        var stillRetryable = second.Failures.Where(f => f.Retryable).ToList();
        if (stillRetryable.Count > 0)
        {
            throw new BulkRetriesExhaustedException(stillRetryable.Count, string.Join(", ", stillRetryable.Take(3).Select(f => f.Id)));
        }

        return new BulkIndexResult(indexed, rejected);
    }

    private async Task<BulkWriteOutcome> SendAsync(
        string indexName,
        IReadOnlyList<SearchDocument> documents,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken) =>
        await pipeline
            .ExecuteAsync(async token => await writer.BulkIndexAsync(indexName, documents, token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    private ResiliencePipeline BuildRequestPipeline(SyncOptions options) =>
        RequestRetry.Build(options.MaxBulkAttempts, options.BulkBackoffBase, options.BulkBackoffCap, timeProvider, logger, "A bulk request");

    private static RejectedDocument ToRejected(BulkItemFailure failure) =>
        new(failure.Id, $"bulk-rejected (status {failure.Status}): {Truncate(failure.Reason)}");

    internal static string Truncate(string value) => value.Length <= 500 ? value : value[..500];
}
