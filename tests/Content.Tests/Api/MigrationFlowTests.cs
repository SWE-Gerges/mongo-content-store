using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Migration;
using Content.Mongo.Coordination;
using Content.Sync.Checkpoints;
using Content.Sync.DeadLetters;
using Content.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace Content.Tests.Api;

/// <summary>Migrations driven over HTTP: real coordinators and runner, a real SQLite source, an in-memory Mongo target.</summary>
public sealed class MigrationFlowTests : IDisposable
{
    private const string Migration = "hotels-from-sqlite";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeWorld _world = new();
    private readonly List<FakeBackedApiFactory> _hosts = [];
    private readonly List<SqliteSource> _sources = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    private SqliteSource Source(int rows)
    {
        var source = SqliteSource.WithHotels(rows);
        _sources.Add(source);
        return source;
    }

    private HttpClient Admin(SqliteSource source, int batchSize = 100, string key = FakeBackedApiFactory.AdminKey)
    {
        var host = new FakeBackedApiFactory(_world).WithMigration(source.Definition(batchSize));
        _hosts.Add(host);
        return host.CreateClientWithKey(key);
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement.Clone();

    private async Task<(string Id, JsonElement Run)> StartAndWaitAsync(HttpClient admin, object? body = null)
    {
        var started = await admin.PostAsJsonAsync("/api/v1/migration/runs", body ?? new { migration = Migration }, _ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await JsonAsync(started);
        var id = accepted.GetProperty("runId").GetString()!;
        started.Headers.Location!.ToString().ShouldBe($"/api/v1/migration/runs/{id}");
        return (id, await WaitWhileActiveAsync(admin, id));
    }

    private async Task<JsonElement> WaitWhileActiveAsync(HttpClient admin, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = await JsonAsync(await admin.GetAsync($"/api/v1/migration/runs/{id}", _ct));
            if (run.GetProperty("state").GetString() is not ("queued" or "running"))
            {
                return run;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the migration did not settle: " + run);
            await Task.Delay(10, _ct);
        }
    }

    [Fact]
    public async Task A_migration_can_be_started_watched_and_verified_through_the_api()
    {
        var source = Source(250);
        var admin = Admin(source);

        var (_, run) = await StartAndWaitAsync(admin);

        run.GetProperty("state").GetString().ShouldBe("completed", run.ToString());
        run.GetProperty("migration").GetString().ShouldBe(Migration);
        run.GetProperty("migrated").GetInt64().ShouldBe(250);
        run.GetProperty("invalid").GetInt64().ShouldBe(0);
        run.GetProperty("batches").GetInt64().ShouldBe(3);
        run.GetProperty("lastKey").GetString().ShouldBe("250");
        var verification = run.GetProperty("verification");
        verification.GetProperty("matches").GetBoolean().ShouldBeTrue();
        verification.GetProperty("sourceCount").GetInt64().ShouldBe(250);
        verification.GetProperty("targetCount").GetInt64().ShouldBe(250);
        _world.MigrationTarget.Ids.Count.ShouldBe(250);
        _world.MigrationTarget.Get(42)!["Name"].AsString.ShouldBe("Hotel 42");
    }

    [Fact]
    public async Task Invalid_rows_are_dead_lettered_under_the_migration_and_listed_through_the_sync_route()
    {
        var source = Source(30);
        source.Execute("UPDATE hotels SET rating = 'five stars' WHERE id = 7");
        source.Execute("UPDATE hotels SET name = NULL WHERE id = 9");
        var admin = Admin(source, batchSize: 10);

        var (_, run) = await StartAndWaitAsync(admin);

        run.GetProperty("state").GetString().ShouldBe("completed", run.ToString());
        run.GetProperty("migrated").GetInt64().ShouldBe(28);
        run.GetProperty("invalid").GetInt64().ShouldBe(2);
        run.GetProperty("verification").GetProperty("matches").GetBoolean().ShouldBeTrue();
        var letters = await JsonAsync(await admin.GetAsync($"/api/v1/sync/deadletters?collection={Uri.EscapeDataString("migration:" + Migration)}", _ct));
        letters.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("docId").GetString()).Order().ShouldBe(["7", "9"]);
        (await _world.DeadLetters.CountAsync("hotels", null, _ct)).ShouldBe(0, "the sync's namespace is untouched");
    }

    [Fact]
    public async Task A_count_mismatch_fails_the_run_and_reports_the_numbers()
    {
        var source = Source(100);
        _world.MigrationTarget.SilentlyDropWhen = document => document["_id"].AsInt64 % 25 == 0;
        var admin = Admin(source);

        var (_, run) = await StartAndWaitAsync(admin);

        run.GetProperty("state").GetString().ShouldBe("failed");
        run.GetProperty("lastError").GetString()!.ShouldContain("Count verification failed");
        var verification = run.GetProperty("verification");
        verification.GetProperty("matches").GetBoolean().ShouldBeFalse();
        verification.GetProperty("sourceCount").GetInt64().ShouldBe(100);
        verification.GetProperty("targetCount").GetInt64().ShouldBe(96);
        verification.GetProperty("difference").GetInt64().ShouldBe(-4);
    }

    [Fact]
    public async Task A_run_that_crashed_is_resumed_by_the_next_start_without_loss_or_duplicates()
    {
        var source = Source(500);
        _world.MigrationTarget.FailUpsertWhen = call => call == 3 ? new IOException("target went away") : null;
        var admin = Admin(source);

        var (_, failed) = await StartAndWaitAsync(admin);

        failed.GetProperty("state").GetString().ShouldBe("failed");
        failed.GetProperty("lastKey").GetString().ShouldBe("200");
        _world.MigrationTarget.Ids.Count.ShouldBe(200);

        _world.MigrationTarget.FailUpsertWhen = null;
        var (_, resumed) = await StartAndWaitAsync(admin);

        resumed.GetProperty("state").GetString().ShouldBe("completed", resumed.ToString());
        resumed.GetProperty("resumedFromKey").GetString().ShouldBe("200");
        _world.MigrationTarget.Ids.Count.ShouldBe(500);
        _world.MigrationTarget.DocumentsWritten.ShouldBe(500);
    }

    [Fact]
    public async Task A_second_start_while_it_runs_is_a_conflict_and_a_run_can_be_cancelled_and_resumed()
    {
        var source = Source(1000);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _world.MigrationTarget.FailUpsertWhen = _ =>
        {
            if (Interlocked.Increment(ref calls) == 3)
            {
                gate.Task.Wait(TimeSpan.FromSeconds(20));
            }

            return null;
        };
        var admin = Admin(source);
        var other = Admin(source);

        var started = await admin.PostAsJsonAsync("/api/v1/migration/runs", new { migration = Migration }, _ct);
        var id = (await JsonAsync(started)).GetProperty("runId").GetString()!;
        var again = await other.PostAsJsonAsync("/api/v1/migration/runs", new { migration = Migration }, _ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var cancel = await other.PostAsync($"/api/v1/migration/runs/{id}/cancel", null, _ct);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(400, _ct); // longer than the heartbeat interval, so the owner has seen the cancel request
        gate.SetResult();
        var cancelled = await WaitWhileActiveAsync(admin, id);

        cancelled.GetProperty("state").GetString().ShouldBe("cancelled", cancelled.ToString());
        (await admin.PostAsync($"/api/v1/migration/runs/{id}/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var checkpoint = await _world.Checkpoints.GetAsync("migration:" + Migration, _ct);
        checkpoint.ShouldNotBeNull("cancelling keeps the checkpoint");

        _world.MigrationTarget.FailUpsertWhen = null;
        var (_, resumed) = await StartAndWaitAsync(admin);
        resumed.GetProperty("state").GetString().ShouldBe("completed", resumed.ToString());
        _world.MigrationTarget.Ids.Count.ShouldBe(1000);
    }

    [Fact]
    public async Task Requests_are_validated_and_unknown_things_are_404()
    {
        var source = Source(5);
        var admin = Admin(source);

        (await admin.PostAsJsonAsync("/api/v1/migration/runs", new { migration = "nope" }, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync("/api/v1/migration/runs", new { migration = (string?)null }, _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/migration/runs", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/migration/runs", new { migration = Migration, batchSize = 0 }, _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/migration/runs/does-not-exist", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync("/api/v1/migration/runs/does-not-exist/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_sync_run_is_not_a_migration_run()
    {
        var source = Source(5);
        var admin = Admin(source);


        var syncRun = new RunRecord("sync-run-1", RunKinds.Sync, "hotel_supplier-a", RunState.Running, "other-instance", DateTimeOffset.UtcNow, null, null, new Dictionary<string, long>(), null, null, false, null);
        await _world.Runs.CreateAsync(syncRun, _ct);

        (await admin.GetAsync($"/api/v1/migration/runs/{syncRun.Id}", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/v1/migration/runs/{syncRun.Id}/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_routes_need_the_admin_key()
    {
        var source = Source(5);

        var reader = Admin(source, key: FakeBackedApiFactory.ReaderKey);

        (await reader.PostAsJsonAsync("/api/v1/migration/runs", new { migration = Migration }, _ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.GetAsync("/api/v1/migration/runs/x", _ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Admin(source, key: "wrong-key-for-tests-0001").GetAsync("/api/v1/migration/runs/x", _ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_host_refuses_to_start_on_a_broken_definition()
    {
        var source = SqliteSource.Create();
        _sources.Add(source);
        var definition = source.Definition();
        using var broken = new FakeBackedApiFactory(_world).WithMigration(definition);
        broken.Settings["Migration:Definitions:0:KeyColumn"] = "id; drop";

        var failure = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => broken.CreateClient());

        failure.Message.ShouldContain("KeyColumn must be a plain column name");
    }

    [Fact]
    public void The_migration_services_resolve_from_the_real_composition()
    {
        var source = Source(1);
        var host = new FakeBackedApiFactory(_world).WithMigration(source.Definition());
        _hosts.Add(host);

        host.Services.GetRequiredService<IMigrationService>().Exists(Migration).ShouldBeTrue();
        host.Services.GetServices<IDbProvider>().Select(p => p.Name).Order().ShouldBe(["sqlite", "sqlserver"]);
    }
}

/// <summary>The migrator against a real SQLite source (but a fake target), without the HTTP layer.</summary>
public sealed class SqliteMigrationTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly SqliteSource _source = SqliteSource.WithHotels(2000);

    public void Dispose() => _source.Dispose();

    private (MigrationRunner Runner, InMemoryMigrationTarget Target, InMemoryCheckpoints Checkpoints, InMemoryDeadLetters DeadLetters) Build()
    {
        var target = new InMemoryMigrationTarget();
        var checkpoints = new InMemoryCheckpoints();
        var deadLetters = new InMemoryDeadLetters();
        var rows = new DbRowSource([new SqliteProvider(), new SqlServerProvider()]);
        return (new MigrationRunner(rows, target, checkpoints, deadLetters, NullLogger<MigrationRunner>.Instance), target, checkpoints, deadLetters);
    }

    [Fact]
    public async Task A_crash_midway_and_a_resume_leave_exactly_the_source_rows_in_the_target()
    {
        var (runner, target, checkpoints, _) = Build();
        var definition = _source.Definition(200);
        target.FailUpsertWhen = call => call == 6 ? new IOException("boom") : null;

        await Should.ThrowAsync<IOException>(() => runner.RunAsync(definition, 200, _ct));

        (await checkpoints.GetAsync(definition.Target, _ct))!.LastId.ShouldBe(new BsonInt64(1000));
        target.Ids.Count.ShouldBe(1000);

        target.FailUpsertWhen = null;
        var summary = await runner.RunAsync(definition, 200, _ct);

        summary.ResumedFromKey.ShouldBe("1000");
        summary.Verification.Matches.ShouldBeTrue(summary.Verification.Describe());
        target.Ids.Order().ShouldBe(Enumerable.Range(1, 2000).Select(i => i.ToString()).Order());
        target.DocumentsWritten.ShouldBe(2000);
    }

    [Fact]
    public async Task A_second_run_over_the_same_source_is_a_no_op_for_the_data()
    {
        var (runner, target, _, _) = Build();
        var definition = _source.Definition(500);
        await runner.RunAsync(definition, 500, _ct);
        var before = target.Ids.Order().Select(id => target.Get(long.Parse(id))!.ToJson()).ToList();

        var again = await runner.RunAsync(definition, 500, _ct);

        again.Verification.Matches.ShouldBeTrue();
        target.Ids.Count.ShouldBe(2000);
        target.Ids.Order().Select(id => target.Get(long.Parse(id))!.ToJson()).ToList().ShouldBe(before);
    }

    [Fact]
    public async Task Text_ratings_and_mixed_group_ids_are_converted_from_the_real_source()
    {
        var (runner, target, _, _) = Build();

        await runner.RunAsync(_source.Definition(), 100, _ct);

        target.Get(3)!["Rating"].ShouldBe(new BsonDouble(4), "stored as the text '4'");
        target.Get(4)!["Rating"].ShouldBe(new BsonDouble(4.5), "stored as the text '4.5'");
        target.Get(5)!["GroupId"].ShouldBe(new BsonString("G5"));
        target.Get(6)!["GroupId"].ShouldBe(new BsonString("6"));
    }

    [Fact]
    public async Task Malformed_source_rows_are_dead_lettered_and_everything_else_arrives()
    {
        var (runner, target, _, deadLetters) = Build();
        _source.Execute("UPDATE hotels SET rating = 'n/a' WHERE id IN (10, 1500)");
        _source.Execute("UPDATE hotels SET name = '' WHERE id = 777");

        var summary = await runner.RunAsync(_source.Definition(300), 300, _ct);

        summary.Migrated.ShouldBe(1997);
        summary.Invalid.ShouldBe(3);
        target.Ids.Count.ShouldBe(1997);
        (await deadLetters.ListAsync(_source.Definition().Target, null, 10, _ct)).Items.Select(l => l.DocId).Order().ShouldBe(["10", "1500", "777"]);
        summary.Verification.Matches.ShouldBeTrue(summary.Verification.Describe());
    }
}
