using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Sync.DeadLetters;
using Content.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Content.Tests.Api;

/// <summary>Sync driven over HTTP against the real coordinators, with two API instances sharing one fake deployment.</summary>
[Collection(ClassMapCollection.Name)]
public sealed class SyncFlowTests : IDisposable
{
    private const string Collection = "hotel_supplier-a";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeWorld _world = new();
    private readonly List<FakeBackedApiFactory> _hosts = [];

    public SyncFlowTests() => _ = TestMappings.Counter;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    private HttpClient Admin()
    {
        var host = new FakeBackedApiFactory(_world);
        _hosts.Add(host);
        return host.CreateClientWithKey(FakeBackedApiFactory.AdminKey);
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement.Clone();

    private async Task<JsonElement> WaitWhileActiveAsync(HttpClient client, string collection = Collection)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await JsonAsync(await client.GetAsync($"/api/v1/sync/{collection}/status", _ct));
            if (status.GetProperty("state").GetString() is not ("queued" or "running"))
            {
                return status;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the run did not settle: " + status);
            await Task.Delay(10, _ct);
        }
    }

    [Fact]
    public async Task A_sync_can_be_started_watched_and_inspected_through_the_api()
    {
        _world.Source.Add(Collection, InMemorySource.Hotels(Enumerable.Range(1, 120).Select(i => (long)i)));
        var admin = Admin();

        var started = await admin.PostAsync($"/api/v1/sync/{Collection}/runs", null, _ct);

        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        started.Headers.Location!.ToString().ShouldBe($"/api/v1/sync/{Collection}/status");
        var status = await WaitWhileActiveAsync(admin);
        status.GetProperty("state").GetString().ShouldBe("completed", status.ToString());
        status.GetProperty("indexed").GetInt64().ShouldBe(120);
        status.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        _world.Index.Ids("content-" + Collection).Count.ShouldBe(120);
        var deadLetters = await JsonAsync(await admin.GetAsync($"/api/v1/sync/deadletters?collection={Collection}", _ct));
        deadLetters.GetProperty("items").GetArrayLength().ShouldBe(0);
        deadLetters.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_second_start_while_a_run_is_active_is_rejected_and_a_run_can_be_cancelled_and_resumed()
    {
        _world.Source.Add(Collection, InMemorySource.Hotels(Enumerable.Range(1, 4000).Select(i => (long)i)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Source.BeforeRead = async (_, read, token) =>
        {
            if (read == 3)
            {
                await gate.Task.WaitAsync(token);
            }
        };
        var admin = Admin();

        (await admin.PostAsJsonAsync($"/api/v1/sync/{Collection}/runs", new { batchSize = 100 }, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var again = await admin.PostAsync($"/api/v1/sync/{Collection}/runs", null, _ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await admin.PostAsync($"/api/v1/sync/{Collection}/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var status = await WaitWhileActiveAsync(admin);
        status.GetProperty("state").GetString().ShouldBe("cancelled", status.ToString());
        status.GetProperty("lastId").GetString().ShouldBe("200", "two batches were checkpointed before the third read was held");
        _world.Index.Ids("content-" + Collection).Count.ShouldBe(200);
        (await admin.PostAsync($"/api/v1/sync/{Collection}/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        _world.Source.BeforeRead = null;
        (await admin.PostAsJsonAsync($"/api/v1/sync/{Collection}/runs", new { batchSize = 1000 }, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        status = await WaitWhileActiveAsync(admin);

        status.GetProperty("state").GetString().ShouldBe("completed", status.ToString());
        _world.Index.Ids("content-" + Collection).Count.ShouldBe(4000);
        _world.Index.IndexOperations.ShouldBe(4000, "the resumed run must not re-index the two finished batches");
    }

    [Fact]
    public async Task A_run_started_on_one_instance_is_visible_and_exclusive_on_the_other()
    {
        _world.Source.Add(Collection, InMemorySource.Hotels(Enumerable.Range(1, 300).Select(i => (long)i)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Source.BeforeRead = async (_, read, token) =>
        {
            if (read == 2)
            {
                await gate.Task.WaitAsync(token);
            }
        };
        var first = Admin();
        var second = Admin();

        (await first.PostAsJsonAsync($"/api/v1/sync/{Collection}/runs", new { batchSize = 100 }, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement seen;
        do
        {
            seen = await JsonAsync(await second.GetAsync($"/api/v1/sync/{Collection}/status", _ct));
            await Task.Delay(10, _ct);
        }
        while (seen.GetProperty("lastId").GetString() != "100" && DateTime.UtcNow < deadline);

        seen.GetProperty("state").GetString().ShouldBe("running");
        seen.GetProperty("indexed").GetInt64().ShouldBe(100);
        (await second.PostAsync($"/api/v1/sync/{Collection}/runs", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        gate.SetResult();
        (await WaitWhileActiveAsync(second)).GetProperty("state").GetString().ShouldBe("completed");
        _world.Index.Ids("content-" + Collection).Count.ShouldBe(300);
    }

    [Fact]
    public async Task Dead_letters_are_listed_newest_first_with_a_cursor_that_walks_every_page_once()
    {
        var clock = new FakeTimeProvider();
        var deadLetters = new InMemoryDeadLetters(clock: clock);
        for (var i = 1; i <= 7; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await deadLetters.WriteAsync(Collection, [new DeadLetterEntry(i.ToString(), "rejected", new string('a', 64))], null, _ct);
        }

        // Two entries share a millisecond, so the cursor must break the tie instead of skipping or repeating one.
        clock.Advance(TimeSpan.FromSeconds(1));
        await deadLetters.WriteAsync(Collection, [new DeadLetterEntry("8", "rejected", "h"), new DeadLetterEntry("9", "rejected", "h")], null, _ct);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await deadLetters.ListAsync(Collection, cursor, 3, _ct);
            seen.AddRange(page.Items.Select(i => i.DocId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(9);
        seen.Distinct().Count().ShouldBe(9);
        seen.Take(2).Order().ShouldBe(["8", "9"]);
        seen[2..].ShouldBe(["7", "6", "5", "4", "3", "2", "1"]);
    }

    [Fact]
    public async Task The_dead_letter_route_pages_with_a_cursor_and_rejects_a_garbage_one()
    {
        for (var i = 1; i <= 5; i++)
        {
            await _world.DeadLetters.WriteAsync(Collection, [new DeadLetterEntry($"d{i}", "rejected", "hash")], null, _ct);
            await Task.Delay(2, _ct);
        }

        var admin = Admin();
        var first = await JsonAsync(await admin.GetAsync($"/api/v1/sync/deadletters?collection={Collection}&pageSize=2", _ct));
        var cursor = first.GetProperty("nextCursor").GetString();

        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        cursor.ShouldNotBeNullOrEmpty();
        var second = await JsonAsync(await admin.GetAsync($"/api/v1/sync/deadletters?collection={Collection}&pageSize=2&cursor={Uri.EscapeDataString(cursor)}", _ct));
        second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("docId").GetString()).ShouldBe(["d3", "d2"]);
        (await admin.GetAsync("/api/v1/sync/deadletters?cursor=garbage", _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
