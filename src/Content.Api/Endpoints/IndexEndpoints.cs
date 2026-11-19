using Content.Api.Contracts;
using Content.Sync;
using Content.Sync.Indexing;
using Content.Sync.State;

namespace Content.Api.Endpoints;

internal static class IndexEndpoints
{
    public static IEndpointRouteBuilder MapIndexEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/index")
            .WithTags("Index")
            .RequireAuthorization(SyncEndpoints.AdminPolicy);

        group.MapPost("/{alias}/rebuild", Rebuild)
            .WithName("RebuildIndex")
            .WithSummary("Builds the collection into a new versioned index, verifies it and switches the alias to it.")
            .WithDescription(
                "Admin key required. The build runs in the background under the run lease of the collection, so it excludes a sync of the "
                + "same collection. If verification refuses the new index the alias stays where it was and the new index is kept for inspection.")
            .Produces<RebuildAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{alias}", GetIndex)
            .WithName("GetIndex")
            .WithSummary("Lists the versions behind an alias: which is current, which are retained for rollback, and the last verification.")
            .Produces<IndexOverviewDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{alias}/rollback", Rollback)
            .WithName("RollbackIndex")
            .WithSummary("Points the alias back at a retained version.")
            .WithDescription("Admin key required. Refused with 409 while a sync or rebuild of the collection is active, or when no retained version exists.")
            .Produces<RollbackResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> Rebuild(
        string alias,
        StartSyncRequest? request,
        ISyncTargetCatalog catalog,
        ISyncCoordinator coordinator,
        IIndexVersioning versioning,
        CancellationToken cancellationToken)
    {
        if (catalog.CollectionForAlias(alias) is not { } collection)
        {
            return UnknownAlias(alias);
        }

        if (SyncEndpoints.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var options = coordinator.CreateRunOptions(
            request?.BatchSize,
            request?.InterBatchDelayMs is { } delay ? TimeSpan.FromMilliseconds(delay) : null);
        var started = await versioning.TryStartRebuildAsync(alias, options, cancellationToken);
        if (started.Outcome == RebuildStartOutcome.AlreadyRunning)
        {
            return ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Collection busy", $"A sync or rebuild of '{collection}' is already active (run {started.RunId}).");
        }

        return TypedResults.Accepted($"/api/v1/index/{alias}", new RebuildAcceptedDto(alias, collection, started.RunId!));
    }

    private static async Task<IResult> GetIndex(
        string alias,
        ISyncTargetCatalog catalog,
        ISyncCoordinator coordinator,
        IIndexVersioning versioning,
        CancellationToken cancellationToken)
    {
        if (catalog.CollectionForAlias(alias) is not { } collection)
        {
            return UnknownAlias(alias);
        }

        // Read the run first: a run that reads as completed has already swapped, so the versions read after it can only be as new or newer.
        var run = await coordinator.GetStatusAsync(collection, cancellationToken);
        var overview = await versioning.GetOverviewAsync(alias, cancellationToken);
        return TypedResults.Ok(IndexOverviewDto.From(overview, collection, run));
    }

    private static async Task<IResult> Rollback(
        string alias,
        RollbackRequest? request,
        ISyncTargetCatalog catalog,
        IIndexVersioning versioning,
        CancellationToken cancellationToken)
    {
        if (catalog.CollectionForAlias(alias) is null)
        {
            return UnknownAlias(alias);
        }

        if (request?.Version is < 1)
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "version must be a positive number.");
        }

        var attempt = await versioning.RollbackAsync(alias, request?.Version, cancellationToken);
        return attempt.Outcome switch
        {
            RollbackOutcome.RolledBack => TypedResults.Ok(new RollbackResultDto(alias, attempt.Result!.IndexName, attempt.Result.Version, attempt.Result.PreviousIndex)),
            RollbackOutcome.Busy => ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Collection busy", attempt.Message!),
            _ => ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Nothing to roll back to", attempt.Message!),
        };
    }

    private static IResult UnknownAlias(string alias) =>
        ContentEndpoints.Problem(StatusCodes.Status404NotFound, "Unknown index alias", $"'{alias}' is not an index alias of this service.");
}
