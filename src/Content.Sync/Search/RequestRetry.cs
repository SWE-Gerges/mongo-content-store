using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Content.Sync.Search;

/// <summary>
/// The retry policy for requests to the search cluster: bounded attempts, exponential backoff with a cap, cancellable,
/// and only for the cluster being unavailable. A rejection retrying cannot fix is raised at once.
/// </summary>
internal static class RequestRetry
{
    public static ResiliencePipeline Build(int attempts, TimeSpan baseDelay, TimeSpan cap, TimeProvider timeProvider, ILogger logger, string what) =>
        attempts <= 1
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder { TimeProvider = timeProvider }
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<SearchIndexUnavailableException>(ex => ex is not BulkRetriesExhaustedException),
                    MaxRetryAttempts = attempts - 1,
                    Delay = baseDelay,
                    MaxDelay = cap,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        logger.LogWarning(
                            args.Outcome.Exception,
                            "{What} failed because the search cluster is unavailable (attempt {Attempt} of {Max}); retrying in {Delay}.",
                            what,
                            args.AttemptNumber + 1,
                            attempts,
                            args.RetryDelay);
                        return default;
                    },
                })
                .Build();
}
