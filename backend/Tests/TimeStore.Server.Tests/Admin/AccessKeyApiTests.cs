using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>Access keys end to end: created through the admin API, used by the AWS SDK over SigV4.</summary>
public sealed class AccessKeyApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private const string Keys = "/api/v1/access-keys";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private HttpClient _admin = null!;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await server.AdminClientAsync();

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreatedKey_Signs_UntilDisabled_AgainWhenEnabled_NeverAfterDelete()
    {
        var response = await _admin.PostAsJsonAsync(Keys, new CreateAccessKeyRequest("e2e"), Json, _ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var key = (await response.Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;

        using var s3 = server.Client(key.AccessKeyId, key.SecretKey);
        await s3.ListBucketsAsync(_ct);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"{Keys}/{key.AccessKeyId}/disable", null, _ct)).StatusCode);
        Assert.Equal("InvalidAccessKeyId", (await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.ListBucketsAsync(_ct))).ErrorCode);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"{Keys}/{key.AccessKeyId}/enable", null, _ct)).StatusCode);
        await s3.ListBucketsAsync(_ct);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Keys}/{key.AccessKeyId}", _ct)).StatusCode);
        Assert.Equal("InvalidAccessKeyId", (await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.ListBucketsAsync(_ct))).ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"{Keys}/{key.AccessKeyId}", _ct)).StatusCode);
    }

    [Fact]
    public async Task List_NeverCarriesSecrets_NorTheRootKey()
    {
        var created = await (await _admin.PostAsJsonAsync(Keys, new CreateAccessKeyRequest(null), Json, _ct))
            .Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct);

        var body = await _admin.GetStringAsync(Keys, _ct);

        Assert.Contains(created!.AccessKeyId, body);
        Assert.DoesNotContain(created.SecretKey, body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TimeStoreServer.AccessKey, body);
    }

    [Fact]
    public async Task RootKey_CannotBeDisabled() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PostAsync($"{Keys}/{TimeStoreServer.AccessKey}/disable", null, _ct)).StatusCode);

    [Fact]
    public async Task MasterKeyFile_IsCreatedInTheDataDirectory() =>
        Assert.True(File.Exists(Path.Combine(server.DataPath, "master.key")));
}
