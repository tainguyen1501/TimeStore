using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.Backups;
using TimeStore.Contracts.Maintenance;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// A backup server whose backups take seconds: Zstandard's slowest level on incompressible data, so a test can kill
/// the process while a run is in the middle of its blobs (no test hook in the server).
/// </summary>
public sealed class SlowBackupServer : BackupServer
{
    protected override void Configure(IDictionary<string, string?> environment)
    {
        base.Configure(environment);
        environment["TimeStore__Backup__CompressionLevel"] = "19";
    }
}

/// <summary>
/// The B1 crash criterion end to end: the process is killed (SIGKILL, no shutdown) while a backup runs, then started
/// again on the same data. The run is interrupted and never re-run, nothing is left behind, every object is intact,
/// the blobs the hold kept are reclaimed, and the next backup works.
/// </summary>
public sealed class BackupCrashTests(SlowBackupServer server) : IClassFixture<SlowBackupServer>
{
    private const string Backups = "/api/v1/backups";
    private const string Bucket = "crash";
    private const int LargeObjects = 6;
    private const int LargeSize = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromSeconds(20);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private string BlobsPath => Path.Combine(server.DataPath, "blobs");

    private string TempPath => Path.Combine(server.DataPath, "tmp");

    [Fact]
    public async Task KillDuringBackup_RestartLeavesServerIntact_RunInterrupted_NothingLeft_DeletionsResume()
    {
        // 1. Data: large random objects (slow to compress at level 19) and one small object to overwrite later.
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var s3 = server.Client())
        {
            await s3.PutBucketAsync(Bucket, _ct);
            for (var i = 0; i < LargeObjects; i++)
                await PutAsync(s3, $"large-{i}.bin", expected[$"large-{i}.bin"] = RandomNumberGenerator.GetBytes(LargeSize));
            await PutAsync(s3, "small.txt", "before the backup"u8.ToArray());
        }

        var blobsBefore = BlobFiles();
        Assert.Equal(LargeObjects + 1, blobsBefore.Count);

        // 2. Start a backup and wait until it is in the middle: snapshot taken, archive being written.
        Guid runId;
        using (var admin = await server.AdminClientAsync())
        {
            var started = await admin.PostAsJsonAsync(Backups, new StartBackupRequest(Compression: "zstd"), Json, _ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            runId = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!.Id;
            await WaitUntilAsync(() => PartialArchives().Length == 1 && SnapshotFiles().Length > 0);
        }

        // 3. While it runs, overwrite an object. The server deletes released blobs at once (no delay), but the hold
        //    must keep the old blob: the snapshot references it.
        using (var s3 = server.Client())
            await PutAsync(s3, "small.txt", expected["small.txt"] = "after the backup started"u8.ToArray());
        var blobsDuring = BlobFiles();
        Assert.Superset(blobsBefore, blobsDuring);
        Assert.Equal(LargeObjects + 2, blobsDuring.Count);
        // The held blob: small.txt's first content, released by the overwrite, still on disk.
        var heldBlob = Assert.Single(blobsDuring, name => Md5Of(name) == Md5("before the backup"u8.ToArray()));

        // 4. Crash. What a killed run leaves: the partial archive, its snapshot and checksum list.
        await server.KillAsync();
        Assert.Single(PartialArchives());
        Assert.NotEmpty(SnapshotFiles());

        // 5. Restart on the same data.
        await server.RestartAsync();

        using var client = await server.AdminClientAsync();
        var run = (await client.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{runId}", Json, _ct))!;
        Assert.Equal((BackupRunStatuses.Interrupted, BackupErrors.Codes.Interrupted), (run.Status, run.ErrorCode));
        Assert.NotNull(run.FinishedAt);
        Assert.True(run.BlobsDone < run.BlobsTotal, $"the run was killed after {run.BlobsDone}/{run.BlobsTotal} blobs");

        // Nothing left behind: no partial archive, no snapshot, no checksum list, no other archive.
        Assert.Empty(PartialArchives());
        Assert.Empty(SnapshotFiles());
        Assert.Empty(Directory.Exists(server.BackupPath) ? Directory.GetFiles(server.BackupPath) : []);

        // Never re-run: it stays interrupted (the worker had the chance to pick it up at startup).
        await Task.Delay(500, _ct);
        Assert.Equal(BackupRunStatuses.Interrupted,
            (await client.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{runId}", Json, _ct))!.Status);

        // 6. The server serves every object as committed before the crash.
        using (var s3 = server.Client())
        {
            foreach (var (key, bytes) in expected)
                Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await ReadAsync(s3, key)));
            await PutAsync(s3, "written-after-restart.txt", "still writable"u8.ToArray());
        }

        // 7. Deletions resume: the blob the hold kept is an orphan now (the in-memory queue died with the process);
        //    the orphan sweep reclaims it and nothing else.
        Assert.Contains(heldBlob, BlobFiles());
        var old = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var file in Directory.EnumerateFiles(BlobsPath, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, old);
        var sweep = await client.PostAsync("/api/v1/maintenance/blob-sweep", null, _ct);
        Assert.Equal(HttpStatusCode.OK, sweep.StatusCode);
        Assert.Equal(1, (await sweep.Content.ReadFromJsonAsync<BlobSweepResponse>(Json, _ct))!.Deleted);
        Assert.DoesNotContain(heldBlob, BlobFiles());
        Assert.Equal(LargeObjects + 2, BlobFiles().Count); // the large objects, small.txt, written-after-restart.txt

        // 8. The next backup runs (no stale active run) and succeeds.
        var next = await client.PostAsJsonAsync(Backups, new StartBackupRequest(Compression: "none"), Json, _ct);
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        var nextId = (await next.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!.Id;
        BackupRunResponse? done = null;
        await WaitUntilAsync(() =>
        {
            done = client.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{nextId}", Json, _ct).GetAwaiter().GetResult();
            return done!.Status is not (BackupRunStatuses.Queued or BackupRunStatuses.Running);
        });
        Assert.Equal(BackupRunStatuses.Succeeded, done!.Status);
        Assert.Equal(LargeObjects + 2, done.BlobsDone);
        Assert.True(File.Exists(Path.Combine(server.BackupPath, done.ArchiveName!)));
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    private string Md5Of(string blobName) => Md5(File.ReadAllBytes(Path.Combine(BlobsPath, blobName[^2..], blobName)));

    private HashSet<string> BlobFiles() =>
        Directory.EnumerateFiles(BlobsPath, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToHashSet()!;

    private string[] PartialArchives() =>
        Directory.Exists(server.BackupPath) ? Directory.GetFiles(server.BackupPath, "*.partial") : [];

    private string[] SnapshotFiles() => Directory.Exists(TempPath) ? Directory.GetFiles(TempPath, "backup-*") : [];

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + RunningTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met in {RunningTimeout}.\n{server.Log}");
            await Task.Delay(20, _ct);
        }
    }

    private Task PutAsync(IAmazonS3 s3, string key, byte[] body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = new MemoryStream(body) }, _ct);

    private async Task<byte[]> ReadAsync(IAmazonS3 s3, string key)
    {
        using var response = await s3.GetObjectAsync(Bucket, key, _ct);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, _ct);
        return buffer.ToArray();
    }
}
