using System.Net;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>A server serving a small UI from a directory (<c>TimeStore:Admin:UiPath</c>), as a custom build would.</summary>
public sealed class UiDirectoryServer : TimeStoreServer, IDisposable
{
    public const string IndexMarker = "<!-- timestore test ui -->";
    public const string AssetName = "app-1a2b3c.js";
    private readonly string _ui = Path.Combine(Path.GetTempPath(), "timestore-ui", Guid.NewGuid().ToString("N"));

    public UiDirectoryServer()
    {
        Directory.CreateDirectory(Path.Combine(_ui, "assets"));
        File.WriteAllText(Path.Combine(_ui, "index.html"), $"<!doctype html><html>{IndexMarker}</html>");
        File.WriteAllText(Path.Combine(_ui, "assets", AssetName), "console.log('timestore')");
    }

    protected override void Configure(IDictionary<string, string?> environment) =>
        environment["TimeStore__Admin__UiPath"] = _ui;

    public void Dispose() => Directory.Delete(_ui, recursive: true);
}

/// <summary>Step 7b: the admin port serves the UI next to the API without shadowing it.</summary>
public sealed class AdminUiTests(UiDirectoryServer server) : IClassFixture<UiDirectoryServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/buckets")]
    [InlineData("/login?redirect=%2Fbuckets")]
    public async Task ClientRoutes_GetThePage_RevalidatedOnEveryLoad(string path)
    {
        using var client = server.AnonymousAdminClient();
        var response = await client.GetAsync(path, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(UiDirectoryServer.IndexMarker, await response.Content.ReadAsStringAsync(_ct));
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task HashedAssets_AreCachedForAYear()
    {
        using var client = server.AnonymousAdminClient();
        var response = await client.GetAsync($"/assets/{UiDirectoryServer.AssetName}", _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.True(response.Headers.CacheControl?.Public);
    }

    [Fact]
    public async Task AMissingFile_Is404_NotThePage()
    {
        using var client = server.AnonymousAdminClient();
        var response = await client.GetAsync("/assets/missing-0000.js", _ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/buckets", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/does-not-exist", HttpStatusCode.Unauthorized)]
    [InlineData("/health/live", HttpStatusCode.OK)]
    public async Task ServerPaths_AreNeverAnsweredWithThePage(string path, HttpStatusCode expected)
    {
        using var client = server.AnonymousAdminClient();
        var response = await client.GetAsync(path, _ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(UiDirectoryServer.IndexMarker, await response.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task ApiResponses_AreNotCompressed_UiFilesAre()
    {
        using var client = await server.AdminClientAsync();
        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/v1/buckets");
        api.Headers.AcceptEncoding.ParseAdd("gzip, br");
        var apiResponse = await client.SendAsync(api, _ct);
        Assert.Empty(apiResponse.Content.Headers.ContentEncoding);

        using var page = new HttpRequestMessage(HttpMethod.Get, "/");
        page.Headers.AcceptEncoding.ParseAdd("gzip");
        var pageResponse = await client.SendAsync(page, _ct);
        Assert.Contains("gzip", pageResponse.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task TheS3Port_NeverServesTheUi()
    {
        using var client = new HttpClient { BaseAddress = new Uri(server.S3Url) };
        var response = await client.GetAsync("/buckets", _ct);
        Assert.DoesNotContain(UiDirectoryServer.IndexMarker, await response.Content.ReadAsStringAsync(_ct));
    }
}
