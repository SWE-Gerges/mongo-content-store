using Content.Mongo.Coordination;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Content.Migration;

public enum MigrationStartOutcome
{
    Started,
    AlreadyRunning,
}

public sealed record MigrationStartResult(MigrationStartOutcome Outcome, string? RunId);

public enum MigrationCancelOutcome
{
    /// <summary>The run was asked to stop.</summary>
    Requested,

    NotFound,

    /// <summary>The run exists but has already finished.</summary>
    NotActive,
}

/// <summary>A migration run as any instance can report it.</summary>
public sealed record MigrationRunStatus(
    string RunId,
    string Migration,
    RunState State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long Migrated,
    long Invalid,
    long Batches,
    string? LastKey,
    string? LastError,
    string? ResumedFromKey,
    MigrationVerification? Verification);

/// <summary>Starts, cancels and reports on migrations. At most one run per migration is active across all instances.</summary>
public interface IMigrationService
{
    bool Exists(string migration);

    int DefaultBatchSize(string migration);

    Task<MigrationStartResult> TryStartAsync(string migration, int? batchSize, CancellationToken cancellationToken);

    /// <returns>Null when no migration run has this id.</returns>
    Task<MigrationRunStatus?> GetRunAsync(string runId, CancellationToken cancellationToken);

    Task<MigrationCancelOutcome> CancelAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>What a migration run is started with.</summary>
public sealed record MigrationRequest(MigrationDefinition Definition, int BatchSize);

internal sealed class MigrationService(RunCoordinator runs, IOptions<MigrationOptions> options) : IMigrationService
{
    public bool Exists(string migration) => options.Value.Find(migration) is not null;

    public int DefaultBatchSize(string migration) => Definition(migration).BatchSize;

    public async Task<MigrationStartResult> TryStartAsync(string migration, int? batchSize, CancellationToken cancellationToken)
    {
        var definition = Definition(migration);
        var started = await runs
            .TryStartAsync(RunKinds.Migration, definition.Target, new MigrationRequest(definition, batchSize ?? definition.BatchSize), cancellationToken)
            .ConfigureAwait(false);
        return new MigrationStartResult(started.Started ? MigrationStartOutcome.Started : MigrationStartOutcome.AlreadyRunning, started.RunId);
    }

    public async Task<MigrationRunStatus?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        var status = await runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return status is { Record.Kind: RunKinds.Migration } ? ToStatus(status) : null;
    }

    public async Task<MigrationCancelOutcome> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        var status = await runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (status is not { Record.Kind: RunKinds.Migration })
        {
            return MigrationCancelOutcome.NotFound;
        }

        return await runs.CancelRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ? MigrationCancelOutcome.Requested
            : MigrationCancelOutcome.NotActive;
    }

    private MigrationDefinition Definition(string migration) =>
        options.Value.Find(migration) ?? throw new ArgumentException($"'{migration}' is not a configured migration.", nameof(migration));

    internal static MigrationRunStatus ToStatus(RunStatus status)
    {
        var record = status.Record;
        var detail = record.Detail;
        MigrationVerification? verification = null;
        if (detail is not null && detail.TryGetValue("verification", out var stored) && stored is BsonDocument document)
        {
            verification = new MigrationVerification(
                document["sourceCount"].ToInt64(),
                document["targetCount"].ToInt64(),
                document["deadLettered"].ToInt64(),
                document["expectedTarget"].ToInt64());
        }

        return new MigrationRunStatus(
            record.Id,
            record.Target[MigrationTargets.Prefix.Length..],
            status.EffectiveState,
            record.RequestedAt,
            record.StartedAt,
            record.FinishedAt,
            record.Counters.GetValueOrDefault("migrated"),
            record.Counters.GetValueOrDefault("invalid"),
            record.Counters.GetValueOrDefault("batches"),
            record.LastId,
            record.LastError,
            detail is not null && detail.TryGetValue("resumedFromKey", out var resumed) && resumed.IsString ? resumed.AsString : null,
            verification);
    }
}

/// <summary>Runs a migration. A count that does not match fails the run, with the numbers in its detail.</summary>
internal sealed class MigrationRunWorker(MigrationRunner runner) : IRunWorker
{
    public string Kind => RunKinds.Migration;

    public async Task<BsonDocument?> ExecuteAsync(RunContext context, CancellationToken cancellationToken)
    {
        var request = (MigrationRequest)context.Parameters!;
        var summary = await runner
            .RunAsync(
                request.Definition,
                request.BatchSize,
                cancellationToken,
                new MigrationHooks
                {
                    OnProgress = (progress, token) => context.ReportAsync(
                        new Dictionary<string, long> { ["migrated"] = progress.Migrated, ["invalid"] = progress.Invalid, ["batches"] = progress.Batches },
                        progress.LastKey,
                        token),
                    BeforeCheckpoint = context.EnsureLeaseAsync,
                },
                context.RunId)
            .ConfigureAwait(false);

        var verification = summary.Verification;
        var detail = new BsonDocument
        {
            { "migrated", summary.Migrated },
            { "invalid", summary.Invalid },
            { "batches", summary.Batches },
            { "resumedFromKey", summary.ResumedFromKey is null ? BsonNull.Value : summary.ResumedFromKey },
            {
                "verification", new BsonDocument
                {
                    { "sourceCount", verification.SourceCount },
                    { "targetCount", verification.TargetCount },
                    { "deadLettered", verification.DeadLettered },
                    { "expectedTarget", verification.ExpectedTarget },
                    { "matches", verification.Matches },
                }
            },
        };

        if (!verification.Matches)
        {
            throw new RunFailedException($"Count verification failed: {verification.Describe()}.", detail);
        }

        return detail;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the relational-to-Mongo migrator: definitions from configuration (validated at startup), the SQLite and
    /// SQL Server providers behind one row source abstraction, the runner and its run worker.
    /// Requires <c>AddContentMongo</c> and <c>AddContentSync</c>.
    /// </summary>
    public static IServiceCollection AddContentMigration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MigrationOptions>().Bind(configuration.GetSection(MigrationOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MigrationOptions>, MigrationOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProvider, SqliteProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProvider, SqlServerProvider>());
        services.TryAddSingleton<IRowSource, DbRowSource>();
        services.TryAddSingleton<IMigrationTarget, MongoMigrationTarget>();
        services.TryAddSingleton<MigrationRunner>();
        services.TryAddSingleton<IMigrationService, MigrationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunWorker, MigrationRunWorker>());
        return services;
    }
}
