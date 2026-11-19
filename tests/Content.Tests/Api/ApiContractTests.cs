using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Store;
using Content.Store.Models;
using Content.Sync.DeadLetters;
using Content.Sync.Configuration;
using Content.Sync.State;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Content.Tests.Api;

public sealed class ApiContractTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Reader() => _factory.CreateClientWithKey(ApiFactory.ReaderKey);

    private HttpClient Admin() => _factory.CreateClientWithKey(ApiFactory.AdminKey);

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(_ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static HotelDocument Hotel(long id, string name = "Grand Example") => new()
    {
        Id = id,
        Code = $"H{id}",
        Name = name,
        CityCode = "C001",
        CountryCode = "XX",
        Rating = 4.5,
        Rooms = [new RoomData { Id = "R1", GroupIdRaw = 7, Name = "Double", Extra = new MongoDB.Bson.BsonDocument("secret", "not exposed") }],
        Providers = [new ProviderRef { Supplier = "supplier-a", HotelId = "1001" }],
    };

    public class Authentication : IDisposable
    {
        private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
        private readonly ApiFactory _factory = new();

        public void Dispose() => _factory.Dispose();

        [Fact]
        public async Task A_missing_key_is_a_401_problem_with_a_correlation_id()
        {
            var response = await _factory.CreateClientWithKey(null).GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement;
            body.GetProperty("status").GetInt32().ShouldBe(401);
            body.GetProperty("correlationId").GetString().ShouldBe(response.Headers.GetValues("X-Correlation-Id").Single());
        }

        [Theory]
        [InlineData("wrong-key-wrong-key-0001")]
        [InlineData("x")]
        [InlineData("reader-key-for-tests-0001 ")]
        public async Task An_unknown_key_is_rejected(string key)
        {
            var response = await _factory.CreateClientWithKey(key).GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task A_reader_key_reads_content_but_is_forbidden_on_sync_routes()
        {
            _factory.Store.GetByIdsAsync("supplier-a", Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns([Hotel(1)]);
            var reader = _factory.CreateClientWithKey(ApiFactory.ReaderKey);

            (await reader.GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var forbidden = await reader.GetAsync("/api/v1/sync/hotels/status", _ct);

            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            forbidden.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        }

        [Fact]
        public async Task An_admin_key_may_read_content_too()
        {
            _factory.Store.GetByIdsAsync("supplier-a", Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns([Hotel(1)]);

            var response = await _factory.CreateClientWithKey(ApiFactory.AdminKey).GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Health_and_openapi_are_anonymous()
        {
            var client = _factory.CreateClientWithKey(null);

            (await client.GetAsync("/health/live", _ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/health/ready", _ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/openapi/v1.json", _ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public void Startup_fails_fast_when_no_admin_key_is_configured()
        {
            using var factory = new ApiFactory();
            factory.Settings.Remove("Auth:AdminKeys:0");

            Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain("Auth:AdminKeys");
        }

        [Fact]
        public void Startup_fails_fast_when_a_key_is_too_short()
        {
            using var factory = new ApiFactory();
            factory.Settings["Auth:ApiKeys:0"] = "short";

            Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain("at least");
        }
    }

    public class Correlation : IDisposable
    {
        private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
        private readonly ApiFactory _factory = new();

        public void Dispose() => _factory.Dispose();

        [Fact]
        public async Task A_supplied_correlation_id_is_echoed()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Correlation-Id", "trace-abc.123");

            var response = await _factory.CreateClient().SendAsync(request, _ct);

            response.Headers.GetValues("X-Correlation-Id").ShouldBe(["trace-abc.123"]);
        }

        [Fact]
        public async Task A_missing_correlation_id_is_generated()
        {
            var response = await _factory.CreateClient().GetAsync("/health/live", _ct);

            response.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
        }

        [Theory]
        [InlineData("has spaces")]
        [InlineData("<script>")]
        [InlineData("this-id-is-far-too-long-this-id-is-far-too-long-this-id-is-far-too-long-xx")]
        public async Task An_unsafe_correlation_id_is_replaced_not_echoed(string unsafeId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", unsafeId);

            var response = await _factory.CreateClient().SendAsync(request, _ct);

            response.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBe(unsafeId);
        }
    }

    public class Errors : IDisposable
    {
        private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
        private readonly ApiFactory _factory = new();

        public void Dispose() => _factory.Dispose();

        [Fact]
        public async Task An_unexpected_exception_is_a_generic_500_problem_that_leaks_nothing()
        {
            _factory.Store.GetByIdsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<HotelDocument>>(new InvalidOperationException("connection string mongodb://user:pw@host failed at Foo.cs:line 42")));

            var response = await _factory.CreateClientWithKey(ApiFactory.ReaderKey).GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var text = await response.Content.ReadAsStringAsync(_ct);
            text.ShouldNotContain("mongodb://");
            text.ShouldNotContain("Foo.cs");
            text.ShouldNotContain("StackTrace", Case.Insensitive);
            JsonDocument.Parse(text).RootElement.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
        }

        [Fact]
        public async Task A_database_timeout_is_a_503()
        {
            _factory.Store.GetByIdsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<HotelDocument>>(new TimeoutException()));

            var response = await _factory.CreateClientWithKey(ApiFactory.ReaderKey).GetAsync("/api/v1/content/supplier-a/hotels?ids=1", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        [Fact]
        public async Task An_unknown_route_is_a_problem_document()
        {
            var response = await _factory.CreateClientWithKey(ApiFactory.ReaderKey).GetAsync("/api/v1/nothing-here", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        }
    }

    [Fact]
    public async Task Hotels_by_ids_returns_the_hotels_without_leaking_the_free_form_extra()
    {
        _factory.Store.GetByIdsAsync("supplier-a", Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 2, 1 })), Arg.Any<CancellationToken>())
            .Returns([Hotel(2), Hotel(1)]);

        var response = await Reader().GetAsync("/api/v1/content/supplier-a/hotels?ids=2,1", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("id").GetInt64()).ShouldBe([1L, 2L]);
        body.TryGetProperty("nextCursor", out _).ShouldBeFalse();
        body.TryGetProperty("ids", out _).ShouldBeFalse();
        var hotel = body.GetProperty("items")[0];
        hotel.GetProperty("name").GetString().ShouldBe("Grand Example");
        hotel.GetProperty("rooms")[0].GetProperty("groupId").GetString().ShouldBe("7");
        hotel.ToString().ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("/api/v1/content/supplier-a/hotels")]
    [InlineData("/api/v1/content/supplier-a/hotels?ids=1&cityCode=C001")]
    [InlineData("/api/v1/content/supplier-a/hotels?ids=1,abc")]
    [InlineData("/api/v1/content/supplier-a/hotels?cityCode=C001&cursor=%25%25")]
    public async Task Bad_hotel_queries_are_400_problems(string url)
    {
        _factory.Store.GetByCityAsync("supplier-a", "C001", Arg.Any<int?>(), "%%", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidCursorException());

        var response = await Reader().GetAsync(url, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_unknown_supplier_is_a_404_problem()
    {
        _factory.Store.GetByIdsAsync("supplier-z", Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new UnknownSupplierException("supplier-z"));

        var response = await Reader().GetAsync("/api/v1/content/supplier-z/hotels?ids=1", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JsonAsync(response)).GetProperty("title").GetString().ShouldBe("Unknown supplier");
    }

    [Fact]
    public async Task City_paging_returns_ids_and_the_next_cursor()
    {
        _factory.Store.GetByCityAsync("supplier-a", "C001", 2, null, Arg.Any<CancellationToken>()).Returns(new CityPage([10, 20], "cursor-1"));
        _factory.Store.GetByCityAsync("supplier-a", "C001", 2, "cursor-1", Arg.Any<CancellationToken>()).Returns(new CityPage([30], null));

        var first = await JsonAsync(await Reader().GetAsync("/api/v1/content/supplier-a/hotels?cityCode=C001&pageSize=2", _ct));
        var last = await JsonAsync(await Reader().GetAsync("/api/v1/content/supplier-a/hotels?cityCode=C001&pageSize=2&cursor=cursor-1", _ct));

        first.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ShouldBe([10L, 20L]);
        first.GetProperty("nextCursor").GetString().ShouldBe("cursor-1");
        first.TryGetProperty("items", out _).ShouldBeFalse();
        last.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ShouldBe([30L]);
        last.TryGetProperty("nextCursor", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Hotel_by_code_is_found_or_a_404()
    {
        _factory.Store.GetByCodesAsync("supplier-b", Arg.Is<IReadOnlyCollection<string>>(c => c.Single() == "AB12"), Arg.Any<CancellationToken>()).Returns([Hotel(5)]);
        _factory.Store.GetByCodesAsync("supplier-b", Arg.Is<IReadOnlyCollection<string>>(c => c.Single() == "NOPE"), Arg.Any<CancellationToken>()).Returns([]);

        var found = await Reader().GetAsync("/api/v1/content/supplier-b/hotels/by-code/AB12", _ct);
        var missing = await Reader().GetAsync("/api/v1/content/supplier-b/hotels/by-code/NOPE", _ct);

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(found)).GetProperty("id").GetInt64().ShouldBe(5);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("", NameSearchMode.Anchored)]
    [InlineData("&mode=anchored", NameSearchMode.Anchored)]
    [InlineData("&mode=contains", NameSearchMode.Contains)]
    [InlineData("&mode=CI", NameSearchMode.CaseInsensitive)]
    public async Task Search_passes_the_mode_to_the_store(string modeQuery, NameSearchMode expected)
    {
        _factory.Store.SearchByNameAsync("grand", "XX", expected, 5, Arg.Any<CancellationToken>())
            .Returns([new HotelSummary(1, "H1", "Grand Example", "C001", "XX", 4.5)]);

        var response = await Reader().GetAsync($"/api/v1/content/hotels/search?name=grand&countryCode=XX&limit=5{modeQuery}", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await JsonAsync(response)).GetProperty("items")[0];
        item.GetProperty("name").GetString().ShouldBe("Grand Example");
        item.GetProperty("rating").GetDouble().ShouldBe(4.5);
    }

    [Fact]
    public async Task Search_rejects_an_unknown_mode_and_store_validation_errors_as_400()
    {
        _factory.Store.SearchByNameAsync("", Arg.Any<string?>(), Arg.Any<NameSearchMode>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidQueryException("name is required."));

        (await Reader().GetAsync("/api/v1/content/hotels/search?name=x&mode=fuzzy", _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var missingName = await Reader().GetAsync("/api/v1/content/hotels/search", _ct);

        missingName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(missingName)).GetProperty("detail").GetString().ShouldBe("name is required.");
    }

    [Fact]
    public async Task Provider_lookup_returns_the_matching_hotels()
    {
        _factory.Store.FindByProviderAsync("supplier-a", "1001", Arg.Any<CancellationToken>()).Returns([Hotel(9)]);

        var response = await Reader().GetAsync("/api/v1/content/hotels/by-provider/supplier-a/1001", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("items")[0].GetProperty("id").GetInt64().ShouldBe(9);
    }

    [Fact]
    public async Task Room_detail_maps_every_outcome_to_its_status()
    {
        var detail = new RoomDetail("R1", "Double", "7", 2, "DBL", null, null, null, null, ["BA"]);
        _factory.Store.GetRoomDetailAsync("supplier-a", 5, "R1", Arg.Any<CancellationToken>()).Returns(new RoomDetailResult(RoomDetailStatus.Found, detail));
        _factory.Store.GetRoomDetailAsync("supplier-a", 6, "R1", Arg.Any<CancellationToken>()).Returns(new RoomDetailResult(RoomDetailStatus.HotelNotFound, null));
        _factory.Store.GetRoomDetailAsync("supplier-a", 5, "R9", Arg.Any<CancellationToken>()).Returns(new RoomDetailResult(RoomDetailStatus.RoomNotFound, null));
        _factory.Store.GetRoomDetailAsync("supplier-b", 5, "R1", Arg.Any<CancellationToken>()).Returns(new RoomDetailResult(RoomDetailStatus.NoMapper, null));

        var ok = await Reader().GetAsync("/api/v1/content/supplier-a/hotels/5/rooms/R1/detail", _ct);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(ok);
        body.GetProperty("supplier").GetString().ShouldBe("supplier-a");
        body.GetProperty("occupancy").GetInt32().ShouldBe(2);
        body.GetProperty("characteristicCodes")[0].GetString().ShouldBe("BA");
        (await Reader().GetAsync("/api/v1/content/supplier-a/hotels/6/rooms/R1/detail", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Reader().GetAsync("/api/v1/content/supplier-a/hotels/5/rooms/R9/detail", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Reader().GetAsync("/api/v1/content/supplier-b/hotels/5/rooms/R1/detail", _ct)).StatusCode.ShouldBe((HttpStatusCode)422);
    }

    private void Targets() => _factory.Coordinator.CreateRunOptions(Arg.Any<int?>(), Arg.Any<TimeSpan?>()).Returns(new SyncOptions());

    private static SyncStatus Status(string collection, SyncState state, string? runId = "run-1") =>
        new(collection, "sync", state, runId, "100", 100, 2, 2, DateTimeOffset.UnixEpoch, null, null);

    [Fact]
    public async Task Starting_a_run_is_202_with_a_status_location()
    {
        Targets();
        _factory.Coordinator.TryStartAsync("hotel_supplier-a", Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>()).Returns(new SyncStartResult(SyncStartOutcome.Started, "run-1"));
        _factory.Coordinator.GetStatusAsync("hotel_supplier-a", Arg.Any<CancellationToken>()).Returns(Status("hotel_supplier-a", SyncState.Queued));

        var response = await Admin().PostAsJsonAsync("/api/v1/sync/hotel_supplier-a/runs", new { batchSize = 250, interBatchDelayMs = 100 }, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().ShouldBe("/api/v1/sync/hotel_supplier-a/status");
        var body = await JsonAsync(response);
        body.GetProperty("state").GetString().ShouldBe("queued");
        body.GetProperty("runId").GetString().ShouldBe("run-1");
        _factory.Coordinator.Received().CreateRunOptions(250, TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Starting_a_run_without_a_body_uses_the_defaults()
    {
        Targets();
        _factory.Coordinator.TryStartAsync("hotels", Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>()).Returns(new SyncStartResult(SyncStartOutcome.Started, "run-1"));
        _factory.Coordinator.GetStatusAsync("hotels", Arg.Any<CancellationToken>()).Returns(Status("hotels", SyncState.Queued));

        var response = await Admin().PostAsync("/api/v1/sync/hotels/runs", null, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Coordinator.Received().CreateRunOptions(null, null);
    }

    [Fact]
    public async Task Starting_a_run_while_one_is_active_is_a_409()
    {
        Targets();
        _factory.Coordinator.TryStartAsync("hotels", Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>()).Returns(new SyncStartResult(SyncStartOutcome.AlreadyRunning, "run-7"));

        var response = await Admin().PostAsync("/api/v1/sync/hotels/runs", null, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(response)).GetProperty("detail").GetString()!.ShouldContain("run-7");
    }

    [Theory]
    [InlineData("{\"batchSize\":0}")]
    [InlineData("{\"batchSize\":1000000}")]
    [InlineData("{\"interBatchDelayMs\":-1}")]
    public async Task Starting_a_run_with_bad_overrides_is_a_400(string json)
    {
        var response = await Admin().PostAsync("/api/v1/sync/hotels/runs", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.Coordinator.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sync_routes_reject_collections_that_are_not_configured()
    {
        (await Admin().PostAsync("/api/v1/sync/hotel_nope/runs", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin().GetAsync("/api/v1/sync/hotel_nope/status", _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin().PostAsync("/api/v1/sync/hotel_nope/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.Coordinator.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Status_reports_state_checkpoint_and_counts()
    {
        _factory.Coordinator.GetStatusAsync("hotel_supplier-b", Arg.Any<CancellationToken>()).Returns(Status("hotel_supplier-b", SyncState.Running));

        var response = await Admin().GetAsync("/api/v1/sync/hotel_supplier-b/status", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("collection").GetString().ShouldBe("hotel_supplier-b");
        body.GetProperty("state").GetString().ShouldBe("running");
        body.GetProperty("lastId").GetString().ShouldBe("100");
        body.GetProperty("indexed").GetInt64().ShouldBe(100);
        body.GetProperty("deadLettered").GetInt64().ShouldBe(2);
        body.GetProperty("deadLettersTotal").GetInt64().ShouldBe(2);
        body.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Cancel_is_202_when_a_run_was_active_and_409_when_not()
    {
        _factory.Coordinator.CancelAsync("hotels", Arg.Any<CancellationToken>()).Returns(true);
        _factory.Coordinator.CancelAsync("hotel_supplier-a", Arg.Any<CancellationToken>()).Returns(false);
        _factory.Coordinator.GetStatusAsync("hotels", Arg.Any<CancellationToken>()).Returns(Status("hotels", SyncState.Running));

        (await Admin().PostAsync("/api/v1/sync/hotels/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Admin().PostAsync("/api/v1/sync/hotel_supplier-a/cancel", null, _ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Dead_letters_are_listed_with_paging()
    {
        _factory.DeadLetters.ListAsync("hotels", "cursor-1", 10, Arg.Any<CancellationToken>())
            .Returns(new DeadLetterPage([new DeadLetterRecord("hotels", "42", "bulk-rejected", DateTimeOffset.UnixEpoch, 3, new string('a', 64))], "cursor-2"));

        var response = await Admin().GetAsync("/api/v1/sync/deadletters?collection=hotels&cursor=cursor-1&pageSize=10", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("nextCursor").GetString().ShouldBe("cursor-2");
        var item = body.GetProperty("items")[0];
        item.GetProperty("docId").GetString().ShouldBe("42");
        item.GetProperty("attempts").GetInt32().ShouldBe(3);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=500")]
    public async Task Dead_letter_paging_is_validated(string query) =>
        (await Admin().GetAsync($"/api/v1/sync/deadletters?{query}", _ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_dead_letter_cursor_the_store_does_not_recognise_is_a_400()
    {
        _factory.DeadLetters.ListAsync(Arg.Any<string?>(), "garbage", Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidCursorException());

        var response = await Admin().GetAsync("/api/v1/sync/deadletters?cursor=garbage", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("title").GetString().ShouldBe("Invalid cursor");
    }

    public class Health : IDisposable
    {
        private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
        private readonly ApiFactory _factory = new();

        public void Dispose() => _factory.Dispose();

        [Fact]
        public async Task Live_does_not_depend_on_anything()
        {
            _factory.MongoProbe.PingAsync(Arg.Any<CancellationToken>()).Throws(new TimeoutException());

            (await _factory.CreateClient().GetAsync("/health/live", _ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Ready_is_healthy_when_both_dependencies_answer()
        {
            var response = await _factory.CreateClient().GetAsync("/health/ready", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_ct)).RootElement;
            body.GetProperty("status").GetString().ShouldBe("healthy");
            body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["mongo", "opensearch"], ignoreOrder: true);
        }

        [Theory]
        [InlineData("mongo")]
        [InlineData("opensearch")]
        public async Task Ready_is_503_naming_the_dependency_that_is_down_without_leaking_the_error(string down)
        {
            var failure = new InvalidOperationException("secret host 10.0.0.5 refused the connection");
            if (down == "mongo")
            {
                _factory.MongoProbe.PingAsync(Arg.Any<CancellationToken>()).Throws(failure);
            }
            else
            {
                _factory.SearchProbe.PingAsync(Arg.Any<CancellationToken>()).Throws(failure);
            }

            var response = await _factory.CreateClient().GetAsync("/health/ready", _ct);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var text = await response.Content.ReadAsStringAsync(_ct);
            text.ShouldNotContain("10.0.0.5");
            var checks = JsonDocument.Parse(text).RootElement.GetProperty("checks").EnumerateArray().ToList();
            checks.Single(c => c.GetProperty("name").GetString() == down).GetProperty("status").GetString().ShouldBe("unhealthy");
            checks.Single(c => c.GetProperty("name").GetString() != down).GetProperty("status").GetString().ShouldBe("healthy");
        }
    }
}
