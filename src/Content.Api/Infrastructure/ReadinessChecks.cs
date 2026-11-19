using Content.Mongo.Health;
using Content.Sync.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Content.Api.Infrastructure;

internal static class Readiness
{
    public const string Tag = "ready";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<HealthCheckResult> RunAsync(Func<CancellationToken, Task> probe, string name, ILogger logger, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await probe(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"{name} is reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Readiness check for {Dependency} failed.", name);
            return HealthCheckResult.Unhealthy($"{name} is not reachable.");
        }
    }
}

/// <summary>Ready only while MongoDB answers a ping.</summary>
internal sealed class MongoReadinessCheck(IMongoHealthProbe probe, ILogger<MongoReadinessCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await Readiness.RunAsync(probe.PingAsync, "MongoDB", logger, cancellationToken).ConfigureAwait(false);
}

/// <summary>Ready only while OpenSearch answers and its cluster is not red.</summary>
internal sealed class SearchReadinessCheck(ISearchHealthProbe probe, ILogger<SearchReadinessCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await Readiness.RunAsync(probe.PingAsync, "OpenSearch", logger, cancellationToken).ConfigureAwait(false);
}
