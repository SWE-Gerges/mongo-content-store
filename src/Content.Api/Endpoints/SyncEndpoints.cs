using Content.Api.Contracts;
using Content.Sync;
using Content.Sync.DeadLetters;
using Content.Sync.State;

namespace Content.Api.Endpoints;

internal static class SyncEndpoints
{
    public const string AdminPolicy = "Admin";

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/sync")
            .WithTags("Sync")
            .RequireAuthorization(AdminPolicy);

        group.MapPost("/{collection}/runs", StartRun)
            .WithName("StartSyncRun")
            .WithSummary("Starts a sync of a collection into its search index, or resumes it from the checkpoint.")
            .WithDescription("Admin key required. The run executes in the background; at most one run per collection is active at a time.")
            .Produces<SyncStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{collection}/status", GetStatus)
            .WithName("GetSyncStatus")
            .WithSummary("Reports the state, last checkpointed id, counts and last error of a collection's sync.")
            .Produces<SyncStatusDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/deadletters", GetDeadLetters)
            .WithName("GetDeadLetters")
            .WithSummary("Lists documents that could not be indexed, newest first, with keyset paging.")
            .WithDescription("Pass the nextCursor of one page as cursor to get the next. The cursor is opaque.")
            .Produces<DeadLetterPageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{collection}/cancel", Cancel)
            .WithName("CancelSyncRun")
            .WithSummary("Asks the active run of a collection to stop; its checkpoint stays at the last completed batch.")
            .Produces<SyncStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    /// <summary>Checks the optional overrides of a sync or rebuild start; null when they are acceptable.</summary>
    internal static IResult? Validate(StartSyncRequest? request)
    {
        if (request?.BatchSize is < 1 or > 100_000)
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "batchSize must be between 1 and 100000.");
        }

        if (request?.InterBatchDelayMs is < 0 or > 60_000)
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "interBatchDelayMs must be between 0 and 60000.");
        }

        return null;
    }

    private static async Task<IResult> StartRun(
        string collection,
        StartSyncRequest? request,
        ISyncTargetCatalog catalog,
        ISyncCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!catalog.IsKnown(collection))
        {
            return UnknownCollection(collection);
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var options = coordinator.CreateRunOptions(
            request?.BatchSize,
            request?.InterBatchDelayMs is { } delay ? TimeSpan.FromMilliseconds(delay) : null);
        var started = await coordinator.TryStartAsync(collection, options, cancellationToken);
        if (started.Outcome == SyncStartOutcome.AlreadyRunning)
        {
            return ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Sync already running", $"A sync of '{collection}' is already active (run {started.RunId}).");
        }

        var status = await coordinator.GetStatusAsync(collection, cancellationToken);
        return TypedResults.Accepted($"/api/v1/sync/{collection}/status", SyncStatusDto.From(status));
    }

    private static async Task<IResult> GetStatus(string collection, ISyncTargetCatalog catalog, ISyncCoordinator coordinator, CancellationToken cancellationToken)
    {
        if (!catalog.IsKnown(collection))
        {
            return UnknownCollection(collection);
        }

        return TypedResults.Ok(SyncStatusDto.From(await coordinator.GetStatusAsync(collection, cancellationToken)));
    }

    private static async Task<IResult> GetDeadLetters(string? collection, string? cursor, int? pageSize, IDeadLetterStore store, CancellationToken cancellationToken)
    {
        var size = pageSize ?? 50;
        if (size is < 1 or > 200)
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "pageSize must be between 1 and 200.");
        }

        var result = await store.ListAsync(collection, cursor, size, cancellationToken);
        return TypedResults.Ok(new DeadLetterPageDto(result.Items.Select(DeadLetterDto.From).ToList(), result.NextCursor));
    }

    private static async Task<IResult> Cancel(string collection, ISyncTargetCatalog catalog, ISyncCoordinator coordinator, CancellationToken cancellationToken)
    {
        if (!catalog.IsKnown(collection))
        {
            return UnknownCollection(collection);
        }

        if (!await coordinator.CancelAsync(collection, cancellationToken))
        {
            return ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Nothing to cancel", $"No sync of '{collection}' is running.");
        }

        var status = await coordinator.GetStatusAsync(collection, cancellationToken);
        return TypedResults.Accepted($"/api/v1/sync/{collection}/status", SyncStatusDto.From(status));
    }

    private static IResult UnknownCollection(string collection) =>
        ContentEndpoints.Problem(StatusCodes.Status404NotFound, "Unknown collection", $"'{collection}' is not a collection that can be synced.");
}
