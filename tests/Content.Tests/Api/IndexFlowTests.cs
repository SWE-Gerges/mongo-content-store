using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Sync.Search;
using Content.Tests.Support;

namespace Content.Tests.Api;

/// <summary>Rebuild, swap and rollback driven over HTTP against the real coordinators and the versioning code.</summary>
[Collection(ClassMapCollection.Name)]
public sealed class IndexFlowTests : IDisposable
{
    private const string Collection = "hotel_supplier-a";
    private const string Alias = "content-hotel_supplier-a";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeWorld _world = new();
    private readonly List<FakeBackedApiFactory> _hosts = [];

    public IndexFlowTests()
    {
        _ = TestMappings.Counter;
        _world.Source.Add(Collection, InMemorySource.Hotels(Enumerable.Range(1, 120).Select(i => (long)i)));
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    private HttpClient Client(string key)
    {
        var host = new FakeBackedApiFactory(_world);
        _hosts.Add(host);
        return host.CreateClientWithKey(key);
    }

    private HttpClient Admin() => Client(FakeBackedApiFactory.AdminKey);

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement.Clone();

    private async Task<JsonElement> RebuildAndWaitAsync(HttpClient admin)
    {
        var started = await admin.PostAsync($"/api/v1/index/{Alias}/rebuild", null, _ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        started.Headers.Location!.ToString().ShouldBe($"/api/v1/index/{Alias}");
        return await WaitWhileActiveAsync(admin);
    }

    private async Task<JsonElement> WaitWhileActiveAsync(HttpClient admin)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var overview = await JsonAsync(await admin.GetAsync($"/api/v1/index/{Alias}", _ct));
            var state = overview.GetProperty("latestRun").ValueKind == JsonValueKind.Null ? null : overview.GetProperty("latestRun").GetProperty("state").GetString();
            if (state is not (null or "queued" or "running"))
            {
                return overview;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the rebuild did not settle: " + overview);
            await Task.Delay(10, _ct);
        }
    }

    [Fact]
    public async Task Rebuild_swap_inspect_and_rollback_work_end_to_end()
    {
        var admin = Admin();

        var first = await RebuildAndWaitAsync(admin);

        first.GetProperty("latestRun").GetProperty("kind").GetString().ShouldBe("rebuild");
        first.GetProperty("latestRun").GetProperty("state").GetString().ShouldBe("completed", first.ToString());
        first.GetProperty("current").GetString().ShouldBe(Alias + "-v1");
        first.GetProperty("lastVerification").GetProperty("passed").GetBoolean().ShouldBeTrue();
        first.GetProperty("retained").GetArrayLength().ShouldBe(0);
        _world.Index.Ids(Alias).Count.ShouldBe(120);

        var second = await RebuildAndWaitAsync(admin);

        second.GetProperty("current").GetString().ShouldBe(Alias + "-v2");
        second.GetProperty("retained").EnumerateArray().Select(e => e.GetString()).ShouldBe([Alias + "-v1"]);
        second.GetProperty("versions").EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("state").GetString())).ShouldBe([(2, "live"), (1, "retired")]);
        _world.Index.Swaps.Count.ShouldBe(2);

        var rolled = await admin.PostAsync($"/api/v1/index/{Alias}/rollback", null, _ct);

        rolled.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(rolled);
        body.GetProperty("current").GetString().ShouldBe(Alias + "-v1");
        body.GetProperty("previousIndex").GetString().ShouldBe(Alias + "-v2");
        var after = await JsonAsync(await admin.GetAsync($"/api/v1/index/{Alias}", _ct));
        after.GetProperty("current").GetString().ShouldBe(Alias + "-v1");
        after.GetProperty("retained").EnumerateArray().Select(e => e.GetString()).ShouldBe([Alias + "-v2"]);
    }

    [Fact]
    public async Task A_build_the_verification_refuses_fails_the_run_keeps_the_alias_and_is_visible_in_the_overview()
    {
        var admin = Admin();
        await RebuildAndWaitAsync(admin);
        _world.Index.SilentlyDropWhen = document => int.Parse(document.Id) > 100;

        var overview = await RebuildAndWaitAsync(admin);

        var run = overview.GetProperty("latestRun");
        run.GetProperty("state").GetString().ShouldBe("failed");
        run.GetProperty("lastError").GetString()!.ShouldContain("failed verification and was not swapped in");
        overview.GetProperty("current").GetString().ShouldBe(Alias + "-v1");
        overview.GetProperty("lastVerification").GetProperty("passed").GetBoolean().ShouldBeFalse();
        overview.GetProperty("lastVerification").GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "count").GetProperty("passed").GetBoolean().ShouldBeFalse();
        overview.GetProperty("versions").EnumerateArray().First().GetProperty("state").GetString().ShouldBe("failed");
        _world.Index.IndexNames.ShouldContain(Alias + "-v2");
        _world.Index.AliasTargets(Alias).ShouldBe([Alias + "-v1"]);
    }

    [Fact]
    public async Task Unknown_aliases_are_a_404_and_a_reader_key_is_refused()
    {
        var admin = Admin();

        (await admin.PostAsync("/api/v1/index/content-nowhere/rebuild", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/v1/index/content-nowhere", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync("/api/v1/index/content-nowhere/rollback", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var reader = Client(FakeBackedApiFactory.ReaderKey);
        (await reader.GetAsync($"/api/v1/index/{Alias}", _ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"/api/v1/index/{Alias}/rebuild", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client(null!).GetAsync($"/api/v1/index/{Alias}", _ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rollback_with_nothing_retained_or_a_bad_version_is_refused()
    {
        var admin = Admin();
        await RebuildAndWaitAsync(admin);

        var none = await admin.PostAsync($"/api/v1/index/{Alias}/rollback", null, _ct);
        var bad = await admin.PostAsJsonAsync($"/api/v1/index/{Alias}/rollback", new { version = 0 }, _ct);

        none.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(none)).GetProperty("title").GetString().ShouldBe("Nothing to roll back to");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _world.Index.AliasTargets(Alias).ShouldBe([Alias + "-v1"]);
    }

    [Fact]
    public async Task A_rebuild_excludes_a_sync_a_second_rebuild_and_a_rollback_while_it_holds_the_lease()
    {
        var admin = Admin();
        await RebuildAndWaitAsync(admin);
        await RebuildAndWaitAsync(admin);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Source.BeforeRead = async (_, _, token) => await gate.Task.WaitAsync(token);
        var other = Admin();

        (await admin.PostAsync($"/api/v1/index/{Alias}/rebuild", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await other.PostAsync($"/api/v1/index/{Alias}/rebuild", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await other.PostAsync($"/api/v1/sync/{Collection}/runs", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var rollback = await other.PostAsync($"/api/v1/index/{Alias}/rollback", null, _ct);
        rollback.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(rollback)).GetProperty("title").GetString().ShouldBe("Collection busy");
        _world.Index.AliasTargets(Alias).ShouldBe([Alias + "-v2"], "the alias did not move while the rebuild held the lease");

        gate.SetResult();
        (await WaitWhileActiveAsync(other)).GetProperty("current").GetString().ShouldBe(Alias + "-v3");
    }

    [Fact]
    public async Task A_rebuild_that_crashed_is_resumed_by_the_next_one_through_the_api()
    {
        _world.Source.Add(Collection, InMemorySource.Hotels(Enumerable.Range(1, 300).Select(i => (long)i)));
        var admin = Admin();
        var calls = 0;
        _world.Index.FailWhen = operation => operation == "bulk" && Interlocked.Increment(ref calls) == 3 ? new SearchIndexException("simulated crash") : null;
        var request = new { batchSize = 100 };

        (await admin.PostAsJsonAsync($"/api/v1/index/{Alias}/rebuild", request, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var failed = await WaitWhileActiveAsync(admin);

        failed.GetProperty("latestRun").GetProperty("state").GetString().ShouldBe("failed");
        failed.GetProperty("current").ValueKind.ShouldBe(JsonValueKind.Null);
        failed.GetProperty("versions").EnumerateArray().Single().GetProperty("state").GetString().ShouldBe("building");

        (await admin.PostAsJsonAsync($"/api/v1/index/{Alias}/rebuild", request, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var done = await WaitWhileActiveAsync(admin);

        done.GetProperty("latestRun").GetProperty("state").GetString().ShouldBe("completed", done.ToString());
        done.GetProperty("current").GetString().ShouldBe(Alias + "-v1");
        _world.Index.Ids(Alias).Count.ShouldBe(300);
        _world.Index.IndexOperations.ShouldBe(300, "the finished batches were not indexed again");
    }
}
