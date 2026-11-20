using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Metrics;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// <c>Blobs:RetainDeletedDays</c> (docs/research/blob-retention.md): a blob released by an overwrite or a delete stays on
/// disk for the retention, across a crash and a restart, and the metrics show what it costs.
/// </summary>
public sealed class BlobRetentionTests(BlobRetentionTests.RetainingServer server) : IClassFixture<BlobRetentionTests.RetainingServer>
{
    private const string Bucket = "retained";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private int BlobFiles => Directory.EnumerateFiles(Path.Combine(server.DataPath, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories).Count();

    [Fact]
    public async Task ReleasedBlobs_OutliveTheGrace_AndACrash()
    {
        using (var s3 = server.Client())
        {
            await s3.PutBucketAsync(Bucket, _ct);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "a", ContentBody = "first" }, _ct);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "a", ContentBody = "second" }, _ct);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "b", ContentBody = "gone" }, _ct);
            await s3.DeleteObjectAsync(Bucket, "b", _ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), _ct); // past the 1 s grace and several collector rounds
        Assert.Equal(3, BlobFiles); // "a" now, plus "first" and "gone" kept for the retention

        await server.KillAsync(); // a crash: nothing in memory survives
        await server.RestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), _ct);
        Assert.Equal(3, BlobFiles);

        using var admin = await server.AdminClientAsync();
        var storage = (await admin.GetFromJsonAsync<MetricsResponse>("/api/v1/metrics", JsonSerializerOptions.Web, _ct))!.Storage;
        Assert.Equal((2L, 9L), (storage.RetainedBlobs, storage.RetainedBytes)); // "first" + "gone"
    }

    public sealed class RetainingServer : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment) =>
            environment["TimeStore__Blobs__RetainDeletedDays"] = "1";
    }
}
