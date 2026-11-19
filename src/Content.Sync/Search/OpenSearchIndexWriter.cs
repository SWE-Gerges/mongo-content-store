using Content.Sync.Configuration;
using Microsoft.Extensions.Options;
using OpenSearch.Client;

namespace Content.Sync.Search;

internal sealed class OpenSearchIndexWriter(IOpenSearchClient client, IOptions<SearchOptions> options) : ISearchIndexWriter
{
    public async Task EnsureIndexAsync(string indexName, CancellationToken cancellationToken)
    {
        var exists = await client.Indices.ExistsAsync(indexName, ct: cancellationToken).ConfigureAwait(false);
        if (exists.Exists)
        {
            return;
        }

        if (!exists.ApiCall.Success && exists.ApiCall.HttpStatusCode is not 404)
        {
            throw OpenSearchErrors.Translate(exists.ApiCall.HttpStatusCode, exists.OriginalException, $"checking whether index '{indexName}' exists", cancellationToken);
        }

        var settings = options.Value;
        var created = await client.Indices.CreateAsync(
                indexName,
                index => index
                    .Settings(s => s.NumberOfShards(settings.Shards).NumberOfReplicas(settings.Replicas))
                    .Map<SearchDocument>(HotelIndexMapping.Describe),
                cancellationToken)
            .ConfigureAwait(false);

        if (created.IsValid || created.ServerError?.Error?.Type == "resource_already_exists_exception")
        {
            return;
        }

        throw OpenSearchErrors.Translate(created.ApiCall.HttpStatusCode, created.OriginalException, $"creating index '{indexName}'", cancellationToken, created.ServerError?.Error?.Reason);
    }

    public async Task<BulkWriteOutcome> BulkIndexAsync(
        string indexName,
        IReadOnlyList<SearchDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return BulkWriteOutcome.AllIndexed(0);
        }

        var response = await client.BulkAsync(
                bulk => bulk.Index(indexName).IndexMany(documents, (op, document) => op.Id(document.Id)),
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.ApiCall.Success)
        {
            throw OpenSearchErrors.Translate(response.ApiCall.HttpStatusCode, response.OriginalException, $"bulk indexing into '{indexName}'", cancellationToken, response.ServerError?.Error?.Reason);
        }

        var failures = response.ItemsWithErrors
            .Select(item => new BulkItemFailure(
                item.Id,
                item.Status,
                $"{item.Error?.Type}: {item.Error?.Reason}",
                OpenSearchErrors.IsTransient(item.Status)))
            .ToList();

        return new BulkWriteOutcome(documents.Count - failures.Count, failures);
    }
}
