using OpenSearch.Client;

namespace Content.Sync.Health;

/// <summary>Checks that the search cluster answers and is not red. Throws when it does not.</summary>
public interface ISearchHealthProbe
{
    Task PingAsync(CancellationToken cancellationToken);
}

internal sealed class OpenSearchHealthProbe(IOpenSearchClient client) : ISearchHealthProbe
{
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        var health = await client.Cluster.HealthAsync(ct: cancellationToken).ConfigureAwait(false);
        if (!health.IsValid)
        {
            throw new InvalidOperationException($"OpenSearch did not answer the cluster health request: {health.ServerError?.Error?.Reason ?? health.OriginalException?.Message ?? "no response"}");
        }

        if (string.Equals(health.Status.ToString(), "red", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OpenSearch cluster health is red.");
        }
    }
}
