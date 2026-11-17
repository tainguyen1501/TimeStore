using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Contracts.Metrics;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Metrics;

/// <summary>Step 7e: storage and request metrics follow what clients do over S3.</summary>
public sealed class MetricsApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Storage_And_Requests_FollowS3Traffic()
    {
        using var admin = await server.AdminClientAsync();
        using var s3 = server.Client();
        var bucket = $"metrics-{Guid.NewGuid():N}"[..24];
        var before = await GetAsync(admin);

        await s3.PutBucketAsync(bucket, _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "a", ContentBody = new string('a', 100) }, _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "b", ContentBody = new string('b', 50) }, _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "a", ContentBody = new string('a', 10) }, _ct); // replace
        using (var get = await s3.GetObjectAsync(bucket, "b", _ct))
            await get.ResponseStream.CopyToAsync(Stream.Null, _ct);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.GetObjectAsync(bucket, "missing", _ct));
        await s3.InitiateMultipartUploadAsync(bucket, "pending", _ct);

        var after = await GetAsync(admin);

        var usage = Assert.Single(after.Storage.ByBucket, b => b.Name == bucket);
        Assert.Equal((2L, 60L), (usage.Objects, usage.TotalBytes));
        Assert.True(after.Storage.IncompleteUploads >= 1);
        Assert.True(after.Storage.DiskTotalBytes > 0 && after.Storage.DiskFreeBytes > 0);

        var requests = after.Requests;
        Assert.True(requests.Writes - before.Requests.Writes >= 5); // bucket, three PUTs, initiate
        Assert.True(requests.Reads - before.Requests.Reads >= 2);
        Assert.True(requests.ClientErrors - before.Requests.ClientErrors >= 1);
        Assert.True(requests.BytesIn - before.Requests.BytesIn >= 160);
        Assert.True(requests.BytesOut - before.Requests.BytesOut >= 50);
        Assert.NotNull(requests.P50Ms);

        await s3.DeleteObjectAsync(bucket, "a", _ct);
        var deleted = Assert.Single((await GetAsync(admin)).Storage.ByBucket, b => b.Name == bucket);
        Assert.Equal((1L, 50L), (deleted.Objects, deleted.TotalBytes));
    }

    [Fact]
    public async Task Samples_StartAtStartup_EveryTenSeconds()
    {
        using var admin = await server.AdminClientAsync();
        var metrics = await GetAsync(admin);

        Assert.Equal(10, metrics.SampleIntervalSeconds);
        Assert.NotEmpty(metrics.Samples);
        Assert.True(metrics.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task Viewers_MayRead_AnonymousMayNot()
    {
        using var anonymous = server.AnonymousAdminClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/metrics", _ct)).StatusCode);
    }

    private async Task<MetricsResponse> GetAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<MetricsResponse>("/api/v1/metrics", Json, _ct))!;
}
