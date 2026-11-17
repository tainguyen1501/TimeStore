using System.Text.Json.Nodes;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

public sealed class OpenApiServer : TimeStoreServer
{
    protected override void Configure(IDictionary<string, string?> environment) =>
        environment["TimeStore__Admin__OpenApi"] = "true";
}

/// <summary>
/// Step 7b: the committed OpenAPI document (<c>docs/api/openapi.v1.json</c>, the source of the frontend types) is
/// exactly what the server publishes. After an admin API change, run with <c>TIMESTORE_UPDATE_OPENAPI=1</c> to rewrite
/// it, then <c>pnpm gen:api</c> in frontend/.
/// </summary>
public sealed class OpenApiDocumentTests(OpenApiServer server) : IClassFixture<OpenApiServer>
{
    private const string UpdateVariable = "TIMESTORE_UPDATE_OPENAPI";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheServedDocument_MatchesTheCommittedSnapshot()
    {
        using var client = server.AnonymousAdminClient();
        var served = await client.GetStringAsync("/openapi/v1.json", _ct);
        var snapshot = SnapshotPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
            await File.WriteAllTextAsync(snapshot, served, _ct);

        var committed = await File.ReadAllTextAsync(snapshot, _ct);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(committed), JsonNode.Parse(served)),
            $"docs/api/openapi.v1.json is out of date. Run the tests with {UpdateVariable}=1, then pnpm gen:api.");
    }

    [Fact]
    public async Task ErrorResponses_AreDocumented()
    {
        using var client = server.AnonymousAdminClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", _ct))!;
        var createBucket = document["paths"]!["/api/v1/buckets"]!["post"]!["responses"]!;
        foreach (var status in new[] { "400", "401", "403", "409" })
            Assert.Equal("#/components/schemas/ErrorResponse",
                createBucket[status]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutTheSetting_NoDocumentIsPublished()
    {
        await using var plain = new TimeStoreServer();
        await plain.InitializeAsync();
        using var client = plain.AnonymousAdminClient();
        var response = await client.GetAsync("/openapi/v1.json", _ct);
        Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static string SnapshotPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "api", "openapi.v1.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("docs/api/openapi.v1.json not found above the test directory.");
    }
}
