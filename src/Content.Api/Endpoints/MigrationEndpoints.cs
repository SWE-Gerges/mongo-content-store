using Content.Api.Contracts;
using Content.Migration;

namespace Content.Api.Endpoints;

internal static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/migration")
            .WithTags("Migration")
            .RequireAuthorization(SyncEndpoints.AdminPolicy);

        group.MapPost("/runs", Start)
            .WithName("StartMigrationRun")
            .WithSummary("Starts a configured relational-to-MongoDB migration, or resumes it from its checkpoint.")
            .WithDescription(
                "Admin key required. The migration is named in the request and defined in configuration (source, query, key column, column "
                + "mapping, target collection). It runs in the background, at most one run per migration is active, and invalid rows are "
                + "dead-lettered without stopping it. When it finishes the source count is compared with the target count.")
            .Produces<MigrationRunDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/runs/{id}", Get)
            .WithName("GetMigrationRun")
            .WithSummary("Reports the state, progress, last checkpointed key, error and count verification of a migration run.")
            .Produces<MigrationRunDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/runs/{id}/cancel", Cancel)
            .WithName("CancelMigrationRun")
            .WithSummary("Asks an active migration run to stop; its checkpoint stays at the last completed batch.")
            .Produces<MigrationRunDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> Start(StartMigrationRequest? request, IMigrationService migrations, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Migration))
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "migration is required.");
        }

        if (request.BatchSize is < 1 or > 100_000)
        {
            return ContentEndpoints.Problem(StatusCodes.Status400BadRequest, "Invalid request", "batchSize must be between 1 and 100000.");
        }

        if (!migrations.Exists(request.Migration))
        {
            return ContentEndpoints.Problem(StatusCodes.Status404NotFound, "Unknown migration", $"'{request.Migration}' is not a configured migration.");
        }

        var started = await migrations.TryStartAsync(request.Migration, request.BatchSize, cancellationToken);
        if (started.Outcome == MigrationStartOutcome.AlreadyRunning)
        {
            return ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Migration already running", $"'{request.Migration}' is already active (run {started.RunId}).");
        }

        var status = await migrations.GetRunAsync(started.RunId!, cancellationToken);
        return TypedResults.Accepted($"/api/v1/migration/runs/{started.RunId}", MigrationRunDto.From(status!));
    }

    private static async Task<IResult> Get(string id, IMigrationService migrations, CancellationToken cancellationToken)
    {
        var status = await migrations.GetRunAsync(id, cancellationToken);
        return status is null ? UnknownRun(id) : TypedResults.Ok(MigrationRunDto.From(status));
    }

    private static async Task<IResult> Cancel(string id, IMigrationService migrations, CancellationToken cancellationToken)
    {
        switch (await migrations.CancelAsync(id, cancellationToken))
        {
            case MigrationCancelOutcome.NotFound:
                return UnknownRun(id);
            case MigrationCancelOutcome.NotActive:
                return ContentEndpoints.Problem(StatusCodes.Status409Conflict, "Nothing to cancel", $"Migration run '{id}' is not active.");
        }

        var status = await migrations.GetRunAsync(id, cancellationToken);
        return TypedResults.Accepted($"/api/v1/migration/runs/{id}", MigrationRunDto.From(status!));
    }

    private static IResult UnknownRun(string id) =>
        ContentEndpoints.Problem(StatusCodes.Status404NotFound, "Unknown run", $"There is no migration run '{id}'.");
}
