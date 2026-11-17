using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Contracts.Policies;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>Step 7g: "Make public" for a bucket or a folder through the admin API, checked with unsigned requests.</summary>
public sealed class PublicAccessApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"pub-{Guid.NewGuid():N}";
    private readonly HttpClient _anonymous = new() { BaseAddress = new Uri(server.S3Url) };
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await server.AdminClientAsync();
        await _s3.PutBucketAsync(_bucket, _ct);
        foreach (var key in new[] { "img/cat.png", "img/sub/dog.png", "docs/a.txt", "root.txt" })
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = key }, _ct);
    }

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        _anonymous.Dispose();
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Path => $"/api/v1/buckets/{_bucket}/public";

    private async Task<IReadOnlyList<string>> SetAsync(string prefix, bool isPublic)
    {
        var response = await _admin.PutAsJsonAsync(Path, new SetPublicAccessRequest(prefix, isPublic), Json, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicAccessResponse>(Json, _ct))!.Prefixes;
    }

    private async Task<HttpStatusCode> ReadAsync(string key) => (await _anonymous.GetAsync($"/{_bucket}/{key}", _ct)).StatusCode;

    [Fact]
    public async Task Folder_ThenBucket_ThenPrivateAgain()
    {
        Assert.Empty((await _admin.GetFromJsonAsync<PublicAccessResponse>(Path, Json, _ct))!.Prefixes);
        Assert.Equal(HttpStatusCode.Forbidden, await ReadAsync("img/cat.png"));

        Assert.Equal(["img/"], await SetAsync("img/", true));
        Assert.Equal(HttpStatusCode.OK, await ReadAsync("img/cat.png"));
        Assert.Equal(HttpStatusCode.OK, await ReadAsync("img/sub/dog.png"));
        Assert.Equal(HttpStatusCode.Forbidden, await ReadAsync("docs/a.txt"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _anonymous.GetAsync($"/{_bucket}?list-type=2", _ct)).StatusCode); // read, not list

        Assert.Equal(["", "img/"], await SetAsync("", true));
        Assert.Equal(HttpStatusCode.OK, await ReadAsync("root.txt"));

        Assert.Equal(["img/"], await SetAsync("", false));
        Assert.Equal(HttpStatusCode.Forbidden, await ReadAsync("root.txt"));
        Assert.Empty(await SetAsync("img/", false));
        Assert.Equal(HttpStatusCode.Forbidden, await ReadAsync("img/cat.png"));
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/v1/buckets/{_bucket}/policy", _ct)).StatusCode); // nothing left
    }

    [Fact]
    public async Task AWrittenPolicy_IsKept()
    {
        var own = $$"""{"Version":"2012-10-17","Statement":[{"Sid":"KeepDocs","Effect":"Deny","Principal":"*","Action":"s3:DeleteObject","Resource":"arn:aws:s3:::{{_bucket}}/docs/*"}]}""";
        await _s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = _bucket, Policy = own }, _ct);

        await SetAsync("docs/", true);
        await SetAsync("docs/", false);

        var policy = (await _s3.GetBucketPolicyAsync(_bucket, _ct)).Policy;
        Assert.Contains("KeepDocs", policy);
        Assert.DoesNotContain("TimeStorePublicRead", policy);
    }

    [Theory]
    [InlineData("img")]
    [InlineData("i*/")]
    public async Task NotAFolder_Is400(string prefix)
    {
        var response = await _admin.PutAsJsonAsync(Path, new SetPublicAccessRequest(prefix, true), Json, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("POLICY_INVALID_PUBLIC_PREFIX", await response.Content.ReadAsStringAsync(_ct));
    }

    [Fact]
    public async Task UnknownBucket_Is404() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PutAsJsonAsync("/api/v1/buckets/no-such-bucket-pub/public", new SetPublicAccessRequest("", true), Json, _ct)).StatusCode);
}
