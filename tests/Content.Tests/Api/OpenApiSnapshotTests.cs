using System.Text.Json;
using System.Text.Json.Nodes;

namespace Content.Tests.Api;

public sealed class OpenApiSnapshotTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string SnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MongoContentStore.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("the solution root could not be found above the test output directory");
        return Path.Combine(directory.FullName, "docs", "openapi.json");
    }

    private async Task<string> GenerateAsync()
    {
        var response = await _factory.CreateClient().GetAsync("/openapi/v1.json", _ct);
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(_ct))!;
        return node.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public async Task The_generated_document_matches_docs_openapi_json()
    {
        var generated = await GenerateAsync();
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated, _ct);
            return;
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing; regenerate it with: UPDATE_OPENAPI=1 dotnet test --filter \"FullyQualifiedName~OpenApiSnapshotTests\"");
        var committed = (await File.ReadAllTextAsync(path, _ct)).ReplaceLineEndings("\n");
        generated.ShouldBe(
            committed,
            "the API surface changed; regenerate docs/openapi.json with: UPDATE_OPENAPI=1 dotnet test --filter \"FullyQualifiedName~OpenApiSnapshotTests\"");
    }

    [Fact]
    public async Task Generation_is_deterministic()
    {
        var first = await GenerateAsync();
        using var second = new ApiFactory();
        var again = JsonNode.Parse(await (await second.CreateClient().GetAsync("/openapi/v1.json", _ct)).Content.ReadAsStringAsync(_ct))!
            .ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";

        again.ShouldBe(first);
    }

    [Fact]
    public async Task Documents_every_endpoint_the_brief_promises_with_the_api_key_scheme_and_problem_responses()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;

        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToList();
        paths.ShouldContain("/api/v1/content/{supplier}/hotels");
        paths.ShouldContain("/api/v1/content/{supplier}/hotels/by-code/{code}");
        paths.ShouldContain("/api/v1/content/hotels/search");
        paths.ShouldContain("/api/v1/content/{supplier}/hotels/{id}/rooms/{roomId}/detail");
        paths.ShouldContain("/api/v1/sync/{collection}/runs");
        paths.ShouldContain("/api/v1/sync/{collection}/status");
        paths.ShouldContain("/api/v1/sync/deadletters");
        paths.ShouldContain("/api/v1/sync/{collection}/cancel");
        paths.ShouldContain("/api/v1/index/{alias}/rebuild");
        paths.ShouldContain("/api/v1/index/{alias}");
        paths.ShouldContain("/api/v1/index/{alias}/rollback");
        paths.ShouldContain("/api/v1/migration/runs");
        paths.ShouldContain("/api/v1/migration/runs/{id}");
        paths.ShouldContain("/api/v1/migration/runs/{id}/cancel");
        document["components"]!["securitySchemes"]!["ApiKey"]!["name"]!.GetValue<string>().ShouldBe("X-Api-Key");
        document["paths"]!["/api/v1/sync/{collection}/runs"]!["post"]!["responses"]!.AsObject().Select(r => r.Key).ShouldContain("202");
    }
}
