using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Objects;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>Step 7f: the object's S3 URL, its public state, and share links (presigned GET URLs) from the admin API.</summary>
public sealed class ObjectSharingApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"share-{Guid.NewGuid():N}";
    private readonly HttpClient _plain = new();
    private HttpClient _admin = null!;

    private const string Key = "docs/a report ü.txt";

    public async ValueTask InitializeAsync()
    {
        _admin = await server.AdminClientAsync();
        await _s3.PutBucketAsync(_bucket, _ct);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = Key, ContentBody = "hello" }, _ct);
    }

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        _plain.Dispose();
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Path(string action) => $"/api/v1/buckets/{_bucket}/objects/{action}";

    private Task<HttpResponseMessage> ShareAsync(HttpClient client, int seconds, string key = Key, string? versionId = null) =>
        client.PostAsJsonAsync(Path("share"), new ShareObjectRequest(key, versionId, seconds), Json, _ct);

    [Fact]
    public async Task Access_GivesTheS3Url_AndTurnsPublicWithAPolicy()
    {
        var access = await _admin.GetFromJsonAsync<ObjectAccessResponse>($"{Path("access")}?key={Uri.EscapeDataString(Key)}", Json, _ct);
        Assert.Equal($"http://127.0.0.1:{server.S3Port}/{_bucket}/docs/a%20report%20%C3%BC.txt", access!.Url);
        Assert.False(access.IsPublic);
        Assert.Equal(HttpStatusCode.Forbidden, (await _plain.GetAsync(access.Url, _ct)).StatusCode);

        await _s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = _bucket,
            Policy = $$"""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::{{_bucket}}/docs/*"}]}""",
        }, _ct);

        access = await _admin.GetFromJsonAsync<ObjectAccessResponse>($"{Path("access")}?key={Uri.EscapeDataString(Key)}", Json, _ct);
        Assert.True(access!.IsPublic);
        Assert.Equal("hello", await _plain.GetStringAsync(access.Url, _ct));
    }

    [Fact]
    public async Task Access_ToAMissingObject_Is404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{Path("access")}?key=nope", _ct)).StatusCode);

    [Fact]
    public async Task ShareLink_ReadsThePrivateObject_WithoutCredentials()
    {
        var response = await ShareAsync(_admin, 3600);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var link = (await response.Content.ReadFromJsonAsync<ShareObjectResponse>(Json, _ct))!;

        Assert.Equal(TimeStoreServer.AccessKey, link.SignedBy);
        Assert.InRange(link.ExpiresAt, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.Equal("hello", await _plain.GetStringAsync(link.Url, _ct));

        // The link is for reading this one object: nothing else.
        var other = link.Url.Replace("a%20report", "another", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await _plain.GetAsync(other, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _plain.DeleteAsync(link.Url, _ct)).StatusCode);
    }

    [Fact]
    public async Task ShareLink_ForAnOlderVersion_ReadsThatVersion()
    {
        await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, _ct);
        var first = await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "v.txt", ContentBody = "one" }, _ct);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "v.txt", ContentBody = "two" }, _ct);

        var link = (await (await ShareAsync(_admin, 60, "v.txt", first.VersionId)).Content.ReadFromJsonAsync<ShareObjectResponse>(Json, _ct))!;
        Assert.Equal("one", await _plain.GetStringAsync(link.Url, _ct));
    }

    [Fact]
    public async Task ShareLinks_DieWithTheKeyThatSignedThem()
    {
        var created = (await (await _admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("sharer", "admin"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var sharer = await server.AdminClientAsync(created.AccessKeyId, created.SecretKey);
        var link = (await (await ShareAsync(sharer, 600)).Content.ReadFromJsonAsync<ShareObjectResponse>(Json, _ct))!;
        Assert.Equal(created.AccessKeyId, link.SignedBy);
        Assert.Equal("hello", await _plain.GetStringAsync(link.Url, _ct));

        await _admin.PostAsync($"/api/v1/access-keys/{created.AccessKeyId}/disable", null, _ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _plain.GetAsync(link.Url, _ct)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(604801)]
    public async Task ShareLink_ExpiryOutsideSevenDays_Is400(int seconds)
    {
        var response = await ShareAsync(_admin, seconds);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("OBJECT_INVALID_SHARE_EXPIRY", await response.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task ShareLink_NeedsAnAdmin_AndAnExistingObject()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await ShareAsync(_admin, 60, "missing.txt")).StatusCode);

        var viewer = (await (await _admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("viewer", "viewer"), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var client = await server.AdminClientAsync(viewer.AccessKeyId, viewer.SecretKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await ShareAsync(client, 60)).StatusCode);
    }
}
