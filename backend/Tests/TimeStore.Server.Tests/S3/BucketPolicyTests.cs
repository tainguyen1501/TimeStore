using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Policies;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>
/// Step 6d: bucket policies over S3 and the admin API; anonymous callers, full and policy-scoped keys, the root key.
/// </summary>
public sealed class BucketPolicyTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private const string Keys = "/api/v1/access-keys";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly AmazonS3Client _root = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"bp-{Guid.NewGuid():N}";
    private readonly HttpClient _anonymous = new() { BaseAddress = new Uri(server.S3Url) };
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await server.AdminClientAsync();
        await _root.PutBucketAsync(_bucket, _ct);
        await _root.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "public/cat.txt", ContentBody = "meow" }, _ct);
        await _root.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "private/dog.txt", ContentBody = "woof" }, _ct);
    }

    public ValueTask DisposeAsync()
    {
        _root.Dispose();
        _anonymous.Dispose();
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Policy(params string[] statements) =>
        $$"""{"Version":"2012-10-17","Statement":[{{string.Join(",", statements)}}]}""";

    private string Statement(string effect, string principal, string action, string resource) =>
        $$"""{"Effect":"{{effect}}","Principal":{{principal}},"Action":"{{action}}","Resource":"arn:aws:s3:::{{_bucket}}{{resource}}"}""";

    private Task PutPolicyAsync(string policy) =>
        _root.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = _bucket, Policy = policy }, _ct);

    private async Task<(AmazonS3Client Client, string Id)> KeyAsync(string scope)
    {
        var response = await _admin.PostAsJsonAsync(Keys, new CreateAccessKeyRequest("policy e2e", S3Scope: scope), Json, _ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var key = (await response.Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        Assert.Equal(scope, key.S3Scope);
        return (server.Client(key.AccessKeyId, key.SecretKey), key.AccessKeyId);
    }

    private static async Task<AmazonS3Exception> DeniedAsync(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(call);
        Assert.Equal("AccessDenied", error.ErrorCode);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        return error;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element("Code")!.Value;

    [Fact]
    public async Task Policy_RoundTripsVerbatim_ReportsStatus_AndDeletes()
    {
        await AssertNoPolicyAsync();

        var policy = Policy(Statement("Allow", "\"*\"", "s3:GetObject", "/public/*"));
        await PutPolicyAsync(policy);
        Assert.Equal(policy, (await _root.GetBucketPolicyAsync(_bucket, _ct)).Policy);
        var status = await _root.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = _bucket }, _ct);
        Assert.True(status.PolicyStatus.IsPublic);

        await _root.DeleteBucketPolicyAsync(_bucket, _ct);
        await AssertNoPolicyAsync();
        await _root.DeleteBucketPolicyAsync(_bucket, _ct); // nothing to delete: still 204
    }

    /// <summary>
    /// The server answers 404 NoSuchBucketPolicy (ceph test_set_get_del_bucket_policy checks it too). AWS SDK v4 does
    /// not throw for it: it returns the error document as the policy text.
    /// </summary>
    private async Task AssertNoPolicyAsync()
    {
        try
        {
            var policy = (await _root.GetBucketPolicyAsync(_bucket, _ct)).Policy;
            Assert.Equal("NoSuchBucketPolicy", System.Xml.Linq.XDocument.Parse(policy).Root!.Element("Code")!.Value);
        }
        catch (AmazonS3Exception missing)
        {
            Assert.Equal("NoSuchBucketPolicy", missing.ErrorCode);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::someone-else/*"}]}""")]
    [InlineData("""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:Teleport","Resource":"arn:aws:s3:::*"}]}""")]
    public async Task InvalidPolicies_AreMalformedPolicy(string policy)
    {
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => PutPolicyAsync(policy));
        Assert.Equal("MalformedPolicy", error.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task NotPrincipal_IsNotImplemented()
    {
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => PutPolicyAsync(
            $$"""{"Version":"2012-10-17","Statement":[{"Effect":"Deny","NotPrincipal":{"AWS":"x"},"Action":"s3:*","Resource":"arn:aws:s3:::{{_bucket}}/*"}]}"""));
        Assert.Equal("NotImplemented", error.ErrorCode);
    }

    [Fact]
    public async Task Anonymous_IsDenied_UntilAPolicyMakesItPublic_AndOnlyForWhatItAllows()
    {
        var before = await _anonymous.GetAsync($"/{_bucket}/public/cat.txt", _ct);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("AccessDenied", await ErrorCodeAsync(before));

        await PutPolicyAsync(Policy(Statement("Allow", "\"*\"", "s3:GetObject", "/public/*")));

        var read = await _anonymous.GetAsync($"/{_bucket}/public/cat.txt", _ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("meow", await read.Content.ReadAsStringAsync(_ct));

        Assert.Equal(HttpStatusCode.Forbidden, (await _anonymous.GetAsync($"/{_bucket}/private/dog.txt", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _anonymous.GetAsync($"/{_bucket}?list-type=2", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _anonymous.GetAsync("/", _ct)).StatusCode); // ListBuckets
        var write = await _anonymous.PutAsync($"/{_bucket}/public/new.txt", new StringContent("x", Encoding.UTF8), _ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Anonymous_UnsupportedOperation_IsDenied_NotNotImplemented()
    {
        await PutPolicyAsync(Policy(Statement("Allow", "\"*\"", "s3:*", "/*")));
        var form = await _anonymous.PostAsync($"/{_bucket}", new MultipartFormDataContent { { new StringContent("k"), "key" } }, _ct);
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode); // browser-form POST upload: not supported, and not public
    }

    [Fact]
    public async Task Anonymous_PublicWrite_StoresAnUnsignedBody()
    {
        await PutPolicyAsync(Policy(Statement("Allow", "\"*\"", "s3:PutObject", "/drop/*")));

        var write = await _anonymous.PutAsync($"/{_bucket}/drop/note.txt", new StringContent("hello", Encoding.UTF8), _ct);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        using var stored = await _root.GetObjectAsync(_bucket, "drop/note.txt", _ct);
        Assert.Equal("hello", await new StreamReader(stored.ResponseStream).ReadToEndAsync(_ct));
    }

    [Fact]
    public async Task ExplicitDeny_StopsAFullKey_ButNeverTheRootKey()
    {
        var (full, id) = await KeyAsync("full");
        using (full)
        {
            await full.GetObjectAsync(_bucket, "private/dog.txt", _ct); // full scope: allowed without a policy

            await PutPolicyAsync(Policy(Statement("Deny", $$"""{"AWS":"{{id}}"}""", "s3:GetObject", "/private/*")));

            await DeniedAsync(() => full.GetObjectAsync(_bucket, "private/dog.txt", _ct));
            using var other = await full.GetObjectAsync(_bucket, "public/cat.txt", _ct); // not denied: still allowed
            using var root = await _root.GetObjectAsync(_bucket, "private/dog.txt", _ct); // root ignores policies
        }
    }

    [Fact]
    public async Task PolicyScopedKey_GetsOnlyWhatAPolicyGrants()
    {
        var (limited, id) = await KeyAsync("policy");
        using (limited)
        {
            await DeniedAsync(() => limited.GetObjectAsync(_bucket, "public/cat.txt", _ct));
            await DeniedAsync(() => limited.ListBucketsAsync(_ct));

            await PutPolicyAsync(Policy(
                Statement("Allow", $$"""{"AWS":"arn:aws:iam::000000000000:user/{{id}}"}""", "s3:GetObject", "/public/*"),
                Statement("Allow", $$"""{"AWS":"{{id}}"}""", "s3:ListBucket", "")));

            using var read = await limited.GetObjectAsync(_bucket, "public/cat.txt", _ct);
            Assert.Equal(2, (await limited.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket }, _ct)).KeyCount);
            await DeniedAsync(() => limited.GetObjectAsync(_bucket, "private/dog.txt", _ct));
            await DeniedAsync(() => limited.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "public/x", ContentBody = "x" }, _ct));
        }
    }

    [Fact]
    public async Task ScopeChange_AppliesFromTheNextRequest()
    {
        var (key, id) = await KeyAsync("full");
        using (key)
        {
            await key.GetObjectAsync(_bucket, "public/cat.txt", _ct);
            var changed = await _admin.PostAsJsonAsync($"{Keys}/{id}/scope", new SetAccessKeyScopeRequest("policy"), Json, _ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal("policy", (await changed.Content.ReadFromJsonAsync<AccessKeyResponse>(Json, _ct))!.S3Scope);
            await DeniedAsync(() => key.GetObjectAsync(_bucket, "public/cat.txt", _ct));

            var invalid = await _admin.PostAsJsonAsync($"{Keys}/{id}/scope", new SetAccessKeyScopeRequest("everything"), Json, _ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task CopyNeedsReadAccessToTheSource()
    {
        var (limited, id) = await KeyAsync("policy");
        using (limited)
        {
            await PutPolicyAsync(Policy(Statement("Allow", $$"""{"AWS":"{{id}}"}""", "s3:PutObject", "/copies/*"),
                Statement("Allow", $$"""{"AWS":"{{id}}"}""", "s3:GetObject", "/public/*")));

            await limited.CopyObjectAsync(_bucket, "public/cat.txt", _bucket, "copies/cat.txt", _ct);
            await DeniedAsync(() => limited.CopyObjectAsync(_bucket, "private/dog.txt", _bucket, "copies/dog.txt", _ct));
        }
    }

    [Fact]
    public async Task DeleteObjects_IsCheckedPerKey()
    {
        var (full, id) = await KeyAsync("full");
        using (full)
        {
            await _root.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "tmp/a", ContentBody = "a" }, _ct);
            await PutPolicyAsync(Policy(Statement("Deny", $$"""{"AWS":"{{id}}"}""", "s3:DeleteObject", "/private/*")));

            var failed = await Assert.ThrowsAsync<DeleteObjectsException>(() => full.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _bucket, Objects = [new KeyVersion { Key = "private/dog.txt" }, new KeyVersion { Key = "tmp/a" }],
            }, _ct));
            var result = failed.Response;

            Assert.Equal(["tmp/a"], result.DeletedObjects.Select(d => d.Key));
            var error = Assert.Single(result.DeleteErrors);
            Assert.Equal(("private/dog.txt", "AccessDenied"), (error.Key, error.Code));
            using var kept = await _root.GetObjectAsync(_bucket, "private/dog.txt", _ct);
        }
    }

    [Fact]
    public async Task AdminApi_SetsReadsAndDeletesThePolicy()
    {
        var path = $"/api/v1/buckets/{_bucket}/policy";
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(path, _ct)).StatusCode);

        var document = Policy(Statement("Allow", "\"*\"", "s3:GetObject", "/public/*"));
        var set = await _admin.PutAsJsonAsync(path, new SetBucketPolicyRequest(document), Json, _ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var stored = (await set.Content.ReadFromJsonAsync<BucketPolicyResponse>(Json, _ct))!;
        Assert.Equal((document, true), (stored.Document, stored.IsPublic));

        var malformed = await _admin.PutAsJsonAsync(path, new SetBucketPolicyRequest("{}"), Json, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(path, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(path, _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/v1/buckets/no-such-bucket-here/policy", _ct)).StatusCode);
    }
}
