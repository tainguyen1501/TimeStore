using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Buckets;
using TimeStore.Contracts.Common;
using TimeStore.Contracts.Objects;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>Step 7d: the object browser endpoints of the admin API, end to end and against the S3 API.</summary>
public sealed class ObjectBrowserApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"browse-{Guid.NewGuid():N}"[..30];
    private HttpClient _admin = null!;

    private string Base => $"/api/v1/buckets/{_bucket}";

    public async ValueTask InitializeAsync()
    {
        _admin = await server.AdminClientAsync();
        (await _admin.PostAsJsonAsync("/api/v1/buckets", new CreateBucketRequest(_bucket), Json, _ct)).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Upload_List_Details_Download_RoundTrip_AndS3SeesTheSameObject()
    {
        const string key = "reports/2026 Q4/résumé #1.txt";
        var body = Encoding.UTF8.GetBytes("hello from the admin UI");

        var written = await PutAsync(key, body, "text/plain");
        Assert.Equal(key, written.Key);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(body)), written.ETag);
        Assert.Null(written.VersionId);

        var root = await ListAsync("");
        Assert.Equal(["reports/"], root.Folders.Select(f => f.Prefix));
        var inner = await ListAsync("reports/2026 Q4/");
        Assert.Equal(key, Assert.Single(inner.Objects).Key);

        var details = (await _admin.GetFromJsonAsync<ObjectDetailsResponse>($"{Base}/objects/details?key={Uri.EscapeDataString(key)}", Json, _ct))!;
        Assert.Equal(body.Length, details.Size);
        Assert.Equal("text/plain", details.ContentType);
        Assert.Null(details.PartsCount);

        var download = await _admin.GetAsync($"{Base}/objects/content?key={Uri.EscapeDataString(key)}", _ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(body, await download.Content.ReadAsByteArrayAsync(_ct));
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("résumé #1.txt", download.Content.Headers.ContentDisposition?.FileNameStar);

        using var s3 = server.Client();
        using var get = await s3.GetObjectAsync(_bucket, key, _ct);
        using var copy = new MemoryStream();
        await get.ResponseStream.CopyToAsync(copy, _ct);
        Assert.Equal(body, copy.ToArray());
    }

    [Fact]
    public async Task Downloads_NeverRunAsPages_OnTheAdminOrigin()
    {
        await PutAsync("evil.html", "<script>alert(document.cookie)</script>"u8.ToArray(), "text/html");

        var download = await _admin.GetAsync($"{Base}/objects/content?key=evil.html", _ct);

        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AFolderMarker_ShowsAsAFolder_AndIsHiddenInsideIt()
    {
        await PutAsync("empty/", [], null);

        Assert.Contains("empty/", (await ListAsync("")).Folders.Select(f => f.Prefix));
        var inside = await ListAsync("empty/");
        Assert.Empty(inside.Objects);
        Assert.Empty(inside.Folders);
    }

    [Fact]
    public async Task Listing_Pages_WithStartAfter()
    {
        for (var i = 0; i < 5; i++)
            await PutAsync($"page/{i}.txt", [(byte)i], null);
        await PutAsync("page/sub/x.txt", [1], null);

        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = await ListAsync("page/", after, limit: 2);
            seen.AddRange(page.Folders.Select(f => f.Prefix).Concat(page.Objects.Select(o => o.Key)));
            after = page.NextStartAfter;
            Assert.Equal(page.IsTruncated, after is not null);
        } while (after is not null);

        Assert.Equal(["page/0.txt", "page/1.txt", "page/2.txt", "page/3.txt", "page/4.txt", "page/sub/"], seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MultipartUpload_ThroughTheAdminApi_ProducesTheWholeObject()
    {
        const string key = "big/file.bin";
        var first = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var second = RandomNumberGenerator.GetBytes(1234);

        var start = await (await _admin.PostAsJsonAsync($"{Base}/uploads", new StartUploadRequest(key, "application/x-test"), Json, _ct))
            .Content.ReadFromJsonAsync<StartUploadResponse>(Json, _ct);
        var parts = new List<UploadedPartResponse>();
        foreach (var (number, bytes) in new[] { (1, first), (2, second) })
        {
            var response = await _admin.PutAsync(
                $"{Base}/uploads/{start!.UploadId}/parts/{number}?key={Uri.EscapeDataString(key)}", new ByteArrayContent(bytes), _ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            parts.Add((await response.Content.ReadFromJsonAsync<UploadedPartResponse>(Json, _ct))!);
        }

        var completed = await _admin.PostAsJsonAsync(
            $"{Base}/uploads/{start!.UploadId}/complete?key={Uri.EscapeDataString(key)}", new CompleteUploadRequest(parts), Json, _ct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.EndsWith("-2", (await completed.Content.ReadFromJsonAsync<ObjectWrittenResponse>(Json, _ct))!.ETag);

        var details = (await _admin.GetFromJsonAsync<ObjectDetailsResponse>($"{Base}/objects/details?key={Uri.EscapeDataString(key)}", Json, _ct))!;
        Assert.Equal(2, details.PartsCount);
        Assert.Equal("application/x-test", details.ContentType);
        var downloaded = await _admin.GetByteArrayAsync($"{Base}/objects/content?key={Uri.EscapeDataString(key)}", _ct);
        Assert.Equal(first.Concat(second), downloaded);
    }

    [Fact]
    public async Task AnAbortedUpload_IsGone()
    {
        var start = await (await _admin.PostAsJsonAsync($"{Base}/uploads", new StartUploadRequest("gone.bin", null), Json, _ct))
            .Content.ReadFromJsonAsync<StartUploadResponse>(Json, _ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Base}/uploads/{start!.UploadId}?key=gone.bin", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"{Base}/uploads/{start.UploadId}?key=gone.bin", _ct)).StatusCode);
    }

    [Fact]
    public async Task ASingleUpload_LargerThanKestrelsDefaultLimit_IsAccepted()
    {
        var body = RandomNumberGenerator.GetBytes(40 * 1024 * 1024);
        var written = await PutAsync("large.bin", body, "application/octet-stream");
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(body)), written.ETag);
    }

    [Fact]
    public async Task DeletingAFolder_RemovesEverythingUnderIt_AcrossPages_AndKeysByName()
    {
        using var s3 = server.Client();
        await Parallel.ForEachAsync(Enumerable.Range(0, 1005), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = _ct },
            async (i, ct) => await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = _bucket, Key = $"tree/{i % 3}/{i:D4}", ContentBody = "x" }, ct));
        await PutAsync("keep/a.txt", [1], null);
        await PutAsync("loose.txt", [1], null);

        var response = await _admin.PostAsJsonAsync($"{Base}/objects/delete", new DeleteObjectsRequest(["loose.txt"], ["tree/"]), Json, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1006, (await response.Content.ReadFromJsonAsync<DeleteObjectsResponse>(Json, _ct))!.Deleted);
        var root = await ListAsync("");
        Assert.Equal(["keep/"], root.Folders.Select(f => f.Prefix));
        Assert.Empty(root.Objects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    public async Task DeletingAPrefix_MustNameAFolder(string prefix)
    {
        var response = await _admin.PostAsJsonAsync($"{Base}/objects/delete", new DeleteObjectsRequest(null, [prefix]), Json, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OBJECT_BROWSER_INVALID_PREFIX", (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!.Code);
    }

    [Fact]
    public async Task AnUnknownBucket_Is404()
    {
        var response = await _admin.GetAsync("/api/v1/buckets/no-such-bucket-here/objects", _ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("BUCKET_NOT_FOUND", (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!.Code);
    }

    [Fact]
    public async Task AViewer_Browses_AndDownloads_ButCannotWrite()
    {
        await PutAsync("shared.txt", "shared"u8.ToArray(), "text/plain");
        var created = await (await _admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("viewer", "viewer"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct);
        using var viewer = await server.AdminClientAsync(created!.AccessKeyId, created.SecretKey);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/objects", _ct)).StatusCode);
        Assert.Equal("shared", await viewer.GetStringAsync($"{Base}/objects/content?key=shared.txt", _ct));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsync($"{Base}/objects/content?key=x.txt", new ByteArrayContent([1]), _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync($"{Base}/objects/delete", new DeleteObjectsRequest(["shared.txt"], null), Json, _ct)).StatusCode);
    }

    private async Task<ObjectWrittenResponse> PutAsync(string key, byte[] body, string? contentType)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var response = await _admin.PutAsync($"{Base}/objects/content?key={Uri.EscapeDataString(key)}", content, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ObjectWrittenResponse>(Json, _ct))!;
    }

    private async Task<ObjectListResponse> ListAsync(string prefix, string? startAfter = null, int? limit = null)
    {
        var query = $"?prefix={Uri.EscapeDataString(prefix)}";
        if (startAfter is not null)
            query += $"&startAfter={Uri.EscapeDataString(startAfter)}";
        if (limit is not null)
            query += $"&limit={limit}";
        return (await _admin.GetFromJsonAsync<ObjectListResponse>($"{Base}/objects{query}", Json, _ct))!;
    }
}
