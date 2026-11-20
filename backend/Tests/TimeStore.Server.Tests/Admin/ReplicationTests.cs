using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Application.Features.Replication;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Common;
using TimeStore.Contracts.Replication;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// Bucket replication end to end (docs/research/replication.md "R1"): a source TimeStore sends every change to a
/// second TimeStore through the S3 API. The tests of this class run one after another and share both servers; each
/// uses buckets of its own.
/// </summary>
public sealed class ReplicationTests(ReplicationTests.Servers servers) : IClassFixture<ReplicationTests.Servers>
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private TimeStoreServer Source => servers.Source;

    private TimeStoreServer Destination => servers.Destination;

    [Fact]
    public async Task SavingARule_TestsTheDestination_AndNeverShowsTheSecret()
    {
        var (source, target) = await BucketsAsync("save");
        using var admin = await Source.AdminClientAsync();

        var refused = await admin.PutAsJsonAsync(Route(source), Rule(target, secret: "wrong-secret"), Json, _ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!;
        Assert.Equal(ReplicationErrors.Codes.DestinationFailed, error.Code);
        Assert.Contains("SignatureDoesNotMatch", error.Message);

        var saved = await admin.PutAsJsonAsync(Route(source), Rule(target), Json, _ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadAsStringAsync(_ct);
        Assert.DoesNotContain(TimeStoreServer.SecretKey, body);
        Assert.Equal(target, (await admin.GetFromJsonAsync<ReplicationResponse>(Route(source), Json, _ct))!.Bucket);

        // Editing without the secret keeps it (same endpoint and key); a new endpoint needs it again.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), Rule(target, secret: null, prefix: "x/"), Json, _ct)).StatusCode);
        var moved = await admin.PutAsJsonAsync(Route(source), Rule(target, secret: null) with { Endpoint = "http://127.0.0.1:1" }, Json, _ct);
        Assert.Equal(ReplicationErrors.Codes.SecretRequired, (await moved.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!.Code);

        Assert.Empty(await ListAsync(target)); // the test object is gone again
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Route(source), _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Route(source), _ct)).StatusCode);
    }

    [Fact]
    public async Task EveryChange_ReachesTheDestination_WithItsHeadersAndETag()
    {
        var (source, target) = await BucketsAsync("changes");
        using var s3 = Source.Client();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "before.txt", ContentBody = "existed before the rule" }, _ct);
        using var admin = await Source.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), Rule(target, replicateDeletes: true), Json, _ct)).StatusCode);

        var put = new PutObjectRequest
        {
            BucketName = source, Key = "dir/a b+ü.txt", ContentBody = "hello", ContentType = "text/x-test",
        };
        put.Metadata.Add("owner", "zoe");
        put.Headers.CacheControl = "max-age=60";
        await s3.PutObjectAsync(put, _ct);
        for (var i = 0; i < 20; i++)
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "hot", ContentBody = $"version {i}" }, _ct);
        await s3.CopyObjectAsync(source, "dir/a b+ü.txt", source, "copy.txt", _ct);
        var multipartETag = await UploadMultipartAsync(s3, source, "big.bin");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "doomed", ContentBody = "x" }, _ct);
        await s3.DeleteObjectAsync(source, "doomed", _ct);

        await WaitForBacklogAsync(admin, source);

        using var replica = Destination.Client();
        Assert.Equal(["before.txt", "big.bin", "copy.txt", "dir/a b+ü.txt", "hot"], await ListAsync(target));
        Assert.Equal("version 19", await ReadAsync(replica, target, "hot"));
        var head = await replica.GetObjectMetadataAsync(target, "dir/a b+ü.txt", _ct);
        Assert.Equal(("text/x-test", "max-age=60", "zoe"), (head.Headers.ContentType, head.Headers.CacheControl, head.Metadata["owner"]));
        Assert.Equal(multipartETag, (await replica.GetObjectMetadataAsync(target, "big.bin", _ct)).ETag);
        Assert.Equal("existed before the rule", await ReadAsync(replica, target, "before.txt"));
    }

    [Fact]
    public async Task WithoutReplicateDeletes_TheDestinationKeepsItsCopy_AndPrefixLimitsTheKeys()
    {
        var (source, target) = await BucketsAsync("keep");
        using var s3 = Source.Client();
        using var admin = await Source.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), Rule(target, prefix: "docs/"), Json, _ct)).StatusCode);

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "docs/a", ContentBody = "a" }, _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "tmp/b", ContentBody = "b" }, _ct);
        await WaitForAsync(async () => (await ListAsync(target)).Contains("docs/a"));
        await s3.DeleteObjectAsync(source, "docs/a", _ct);
        await WaitForBacklogAsync(admin, source);

        Assert.Equal(["docs/a"], await ListAsync(target));
    }

    [Fact]
    public async Task ADestinationDown_AndASourceCrash_LoseNothing()
    {
        var (source, target) = await BucketsAsync("crash");
        using (var admin = await Source.AdminClientAsync())
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), Rule(target), Json, _ct)).StatusCode);

        await Destination.KillAsync();
        try
        {
            using (var s3 = Source.Client())
            {
                for (var i = 0; i < 5; i++)
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = $"k{i}", ContentBody = $"{i}" }, _ct);
            }

            using (var admin = await Source.AdminClientAsync())
            {
                // Temporary: the rule pauses, nothing fails.
                await WaitForAsync(async () => (await GetRuleAsync(admin, source)).LastError is not null);
                var paused = await GetRuleAsync(admin, source);
                Assert.Equal((5L, 0L), (paused.Backlog.Pending, paused.Backlog.Failed));
            }

            await Source.KillAsync(); // a crash between the commits and the sends
            await Source.RestartAsync();
        }
        finally
        {
            await Destination.RestartAsync();
        }

        using var after = await Source.AdminClientAsync();
        await WaitForBacklogAsync(after, source);
        Assert.Equal(["k0", "k1", "k2", "k3", "k4"], await ListAsync(target));
        Assert.Null((await GetRuleAsync(after, source)).LastError);
    }

    [Fact]
    public async Task RefusedCredentials_FailTheChanges_UntilARetry()
    {
        var (source, target) = await BucketsAsync("refused");
        using var destinationAdmin = await Destination.AdminClientAsync();
        var created = await destinationAdmin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("replication"), Json, _ct);
        var key = (await created.Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
        using var admin = await Source.AdminClientAsync();
        var rule = Rule(target, secret: key.SecretKey) with { AccessKeyId = key.AccessKeyId };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), rule, Json, _ct)).StatusCode);

        Assert.True((await destinationAdmin.PostAsync($"/api/v1/access-keys/{key.AccessKeyId}/disable", null, _ct)).IsSuccessStatusCode);
        using var s3 = Source.Client();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "a", ContentBody = "a" }, _ct);
        await WaitForAsync(async () => (await GetRuleAsync(admin, source)).Backlog.Failed == 1);
        var failure = Assert.Single((await GetRuleAsync(admin, source)).Backlog.Failures);
        Assert.Contains("403", failure.Error);

        Assert.True((await destinationAdmin.PostAsync($"/api/v1/access-keys/{key.AccessKeyId}/enable", null, _ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(Route(source) + "/retry", null, _ct)).StatusCode);
        await WaitForBacklogAsync(admin, source);
        Assert.Equal(["a"], await ListAsync(target));
    }

    [Fact]
    public async Task Resync_SendsEverythingAgain_ToADestinationThatLostData_AndMetricsShowTheRule()
    {
        var (source, target) = await BucketsAsync("resync");
        using var s3 = Source.Client();
        using var admin = await Source.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Route(source), Rule(target), Json, _ct)).StatusCode);
        foreach (var key in new[] { "a", "b" })
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = key, ContentBody = key }, _ct);
        await WaitForBacklogAsync(admin, source);

        using (var replica = Destination.Client())
            await replica.DeleteObjectAsync(target, "a", _ct); // the destination loses an object
        Assert.Equal(["b"], await ListAsync(target));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(Route(source) + "/resync", null, _ct)).StatusCode);
        await WaitForBacklogAsync(admin, source);
        Assert.Equal(["a", "b"], await ListAsync(target));

        var metrics = (await admin.GetFromJsonAsync<TimeStore.Contracts.Metrics.MetricsResponse>("/api/v1/metrics", Json, _ct))!;
        var rule = Assert.Single(metrics.Replication, r => r.SourceBucket == source);
        Assert.Equal((target, 0L, 0L), (rule.Bucket, rule.Backlog.Pending, rule.Backlog.Failed));
        Assert.NotNull(rule.LastSuccessAt);
    }

    private static string Route(string bucket) => $"/api/v1/buckets/{bucket}/replication";

    private SetReplicationRequest Rule(
        string target, string? secret = TimeStoreServer.SecretKey, bool replicateDeletes = false, string? prefix = null) =>
        new(Destination.S3Url, target, TimeStoreServer.Region, TimeStoreServer.AccessKey, secret, prefix, replicateDeletes);

    private async Task<(string Source, string Target)> BucketsAsync(string name)
    {
        using var s3 = Source.Client();
        using var replica = Destination.Client();
        await s3.PutBucketAsync($"{name}-src", _ct);
        await replica.PutBucketAsync($"{name}-dst", _ct);
        return ($"{name}-src", $"{name}-dst");
    }

    private async Task<ReplicationResponse> GetRuleAsync(HttpClient admin, string bucket) =>
        (await admin.GetFromJsonAsync<ReplicationResponse>(Route(bucket), Json, _ct))!;

    private async Task WaitForBacklogAsync(HttpClient admin, string bucket) =>
        await WaitForAsync(async () => (await GetRuleAsync(admin, bucket)).Backlog is { Pending: 0, Failed: 0 });

    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for replication");
            await Task.Delay(100, _ct);
        }
    }

    private async Task<List<string>> ListAsync(string bucket)
    {
        using var replica = Destination.Client();
        var listed = await replica.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, _ct);
        return (listed.S3Objects ?? []).Select(static o => o.Key).ToList();
    }

    private async Task<string> ReadAsync(IAmazonS3 client, string bucket, string key)
    {
        using var response = await client.GetObjectAsync(bucket, key, _ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(_ct);
    }

    /// <summary>Two parts: 5 MiB (the minimum) and 1 MiB. Returns the source's <c>md5-2</c> ETag.</summary>
    private async Task<string> UploadMultipartAsync(IAmazonS3 s3, string bucket, string key)
    {
        var upload = await s3.InitiateMultipartUploadAsync(bucket, key, _ct);
        var etags = new List<PartETag>();
        foreach (var (number, size) in new[] { (1, 5 * 1024 * 1024), (2, 1024 * 1024) })
        {
            var data = new byte[size];
            Random.Shared.NextBytes(data);
            var part = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = key, UploadId = upload.UploadId, PartNumber = number, InputStream = new MemoryStream(data),
            }, _ct);
            etags.Add(new PartETag(number, part.ETag));
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = etags,
        }, _ct);
        return done.ETag;
    }

    /// <summary>A destination server and a source server that polls often and backs off briefly.</summary>
    public sealed class Servers : IAsyncLifetime
    {
        public TimeStoreServer Destination { get; } = new();

        public TimeStoreServer Source { get; } = new FastReplicationServer();

        public async ValueTask InitializeAsync()
        {
            await Destination.InitializeAsync();
            await Source.InitializeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await Destination.DisposeAsync();
        }
    }

    private sealed class FastReplicationServer : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment)
        {
            environment["TimeStore__Replication__PollSeconds"] = "1";
            environment["TimeStore__Replication__MaxBackoffSeconds"] = "1";
            environment["TimeStore__Replication__BatchDelayMilliseconds"] = "50";
        }
    }
}
