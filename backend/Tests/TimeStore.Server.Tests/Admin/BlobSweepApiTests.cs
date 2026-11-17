using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using TimeStore.Contracts.Maintenance;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>The orphan-blob sweep end to end, triggered through the admin API (own server: it ages every blob).</summary>
public sealed class BlobSweepApiTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sweep_RemovesOrphans_KeepsObjectsAndForeignFiles()
    {
        using var s3 = server.Client();
        await s3.PutBucketAsync("sweep", _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "sweep", Key = "kept.txt", ContentBody = "still here" }, _ct);

        var blobs = Path.Combine(server.DataPath, "blobs");
        var orphanName = Guid.CreateVersion7().ToString("N");
        var orphan = Path.Combine(blobs, orphanName[^2..], orphanName); // as a crash before the commit leaves it
        var foreign = Path.Combine(blobs, "00", "README");
        await File.WriteAllTextAsync(orphan, "lost", _ct);
        await File.WriteAllTextAsync(foreign, "not a blob", _ct);
        var old = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var file in Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, old);

        using var admin = await server.AdminClientAsync();
        var response = await admin.PostAsync("/api/v1/maintenance/blob-sweep", null, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<BlobSweepResponse>(JsonSerializerOptions.Web, _ct))!;
        Assert.Equal((2, 1, 4L), (report.Scanned, report.Deleted, report.DeletedBytes));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(foreign));
        using var get = await s3.GetObjectAsync("sweep", "kept.txt", _ct);
        Assert.Equal("still here", await new StreamReader(get.ResponseStream).ReadToEndAsync(_ct));
    }
}
