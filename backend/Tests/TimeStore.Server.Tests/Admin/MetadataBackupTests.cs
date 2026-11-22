using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Application.Features.Backups;
using TimeStore.Blob.FileSystem;
using TimeStore.Contracts.Backups;
using TimeStore.Contracts.Replication;
using TimeStore.Domain.Backups;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// Metadata-only backups (docs/research/blob-retention.md, B5b) on a server that keeps released blobs: a backup of the
/// metadata alone, then overwrites and deletes, then <c>restore --metadata</c> with the server stopped puts the server
/// back at the backup's instant. A restore refuses a running server, missing blobs and a second run of the same
/// archive (<c>--once</c>).
/// </summary>
public sealed class MetadataBackupTests(MetadataBackupTests.RetainingServer server) : IClassFixture<MetadataBackupTests.RetainingServer>
{
    private const string Bucket = "undo";
    private const string Backups = "/api/v1/backups";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestoringMetadata_PutsTheServerBackAtTheBackup_AndRefusesWhatItCannotDo()
    {
        using (var s3 = server.Client())
        {
            await s3.PutBucketAsync(Bucket, _ct);
            await PutAsync(s3, "a.txt", "first");
            await PutAsync(s3, "b.txt", "kept");
        }

        var first = await BackUpAsync();

        using (var s3 = server.Client())
        {
            await PutAsync(s3, "a.txt", "second");          // overwrite: "first" is released, kept by the retention
            await s3.DeleteObjectAsync(Bucket, "b.txt", _ct); // delete: "kept" too
            await PutAsync(s3, "c.txt", "after the backup");
        }

        // Never under a running server.
        var running = await RestoreAsync(first);
        Assert.Equal(1, running.ExitCode);
        Assert.Contains(BackupErrors.Codes.RestoreInUse, running.Output);

        // A full restore refuses a metadata-only archive.
        var empty = Path.Combine(Path.GetTempPath(), "timestore-e2e", Guid.NewGuid().ToString("N"));
        var full = await TimeStoreServer.RunCommandAsync(["restore", "--archive", first, "--data-path", empty], _ct);
        Assert.Equal(1, full.ExitCode);
        Assert.Contains(BackupErrors.Codes.RestoreWrongContent, full.Output);

        await server.KillAsync();
        var restored = await RestoreAsync(first, "--once");
        Assert.True(restored.ExitCode == 0, restored.Output);
        // Every referenced blob is there (the count depends on what the other tests of this class did before).
        Assert.Matches(@"(\d+) of \1 referenced blobs present", restored.Output);
        var again = await RestoreAsync(first, "--once");
        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Contains("Restore skipped", again.Output);
        await server.RestartAsync();

        using (var s3 = server.Client())
        {
            Assert.Equal("first", await ReadAsync(s3, "a.txt"));
            Assert.Equal("kept", await ReadAsync(s3, "b.txt"));
            var gone = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.GetObjectAsync(Bucket, "c.txt", _ct));
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

            // A blob the next backup needs disappears (its retention ran out): the restore names it and changes nothing.
            var second = await BackUpAsync();
            await s3.DeleteObjectAsync(Bucket, "b.txt", _ct);
            await server.KillAsync();
            File.Delete(BlobHolding("kept"));

            var missing = await RestoreAsync(second);
            Assert.Equal(1, missing.ExitCode);
            Assert.Contains(BackupErrors.Codes.RestoreMissingBlobs, missing.Output);
            Assert.Contains($"{Bucket}/b.txt", missing.Output);

            var anyway = await RestoreAsync(second, "--allow-missing");
            Assert.True(anyway.ExitCode == 0, anyway.Output);
            Assert.Contains("1 MISSING", anyway.Output);
            await server.RestartAsync();
        }

        using (var s3 = server.Client())
            Assert.Equal("first", await ReadAsync(s3, "a.txt"));
    }

    [Fact]
    public async Task AMetadataOnlyBackup_IsSmall_AndListsWhatItReferences()
    {
        using (var s3 = server.Client())
        {
            await s3.PutBucketAsync("sizes", _ct);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = "sizes", Key = "big", InputStream = new MemoryStream(new byte[2_000_000]) }, _ct);
        }

        using var backup = server.BackupClient();
        var run = await RunAsync(backup);

        Assert.Equal(BackupContents.Metadata, run.Content);
        Assert.Equal((0L, 0L), (run.BlobsTotal, run.BytesTotal));
        Assert.True(run.ArchiveSize < 200_000, $"archive of {run.ArchiveSize} bytes");
    }

    [Fact]
    public async Task RestoringMetadata_NamesTheReplicationRulesItDrops()
    {
        // The server replicates to itself: a rule needs a destination that answers.
        using (var s3 = server.Client())
        {
            await s3.PutBucketAsync("rules-src", _ct);
            await s3.PutBucketAsync("rules-dst", _ct);
        }
        using (var admin = await server.AdminClientAsync())
        {
            var rule = new SetReplicationRequest(server.S3Url, "rules-dst", TimeStoreServer.Region, TimeStoreServer.AccessKey,
                TimeStoreServer.SecretKey, "docs/", ReplicateDeletes: false);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RulesRoute, rule, Json, _ct)).StatusCode);
        }

        var archive = await BackUpAsync();
        await server.KillAsync(); // the rule may still be in the WAL only: read anyway
        // The other test of this class deletes a blob on purpose: whatever ran before, restore.
        var restored = await RestoreAsync(archive, "--allow-missing");
        await server.RestartAsync();

        Assert.True(restored.ExitCode == 0, restored.Output);
        Assert.Contains("replication rules NOT restored", restored.Output);
        Assert.Contains($"rules-src → {server.S3Url}/rules-dst (prefix docs/, deletes not copied)", restored.Output);
        using (var admin = await server.AdminClientAsync())
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(RulesRoute, _ct)).StatusCode);
    }

    private const string RulesRoute = "/api/v1/buckets/rules-src/replication";

    private async Task<string> BackUpAsync()
    {
        using var backup = server.BackupClient();
        var run = await RunAsync(backup);
        // Off the data volume, as an operator would keep it.
        var copy = Path.Combine(Path.GetTempPath(), "timestore-e2e", Guid.NewGuid().ToString("N") + "-" + run.ArchiveName);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(Path.Combine(server.DataPath, "backups", run.ArchiveName!), copy);
        return copy;
    }

    private async Task<BackupRunResponse> RunAsync(HttpClient backup)
    {
        var started = await backup.PostAsJsonAsync(Backups, new StartBackupRequest(Content: BackupContents.Metadata), Json, _ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!.Id;
        BackupRunResponse? run = null;
        await TimeStoreServer.WaitUntilAsync(() =>
        {
            run = backup.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{id}", Json, _ct).GetAwaiter().GetResult();
            return run!.Status is not (BackupRunStatuses.Queued or BackupRunStatuses.Running);
        }, _ct);
        Assert.True(run!.Status == BackupRunStatuses.Succeeded, $"{run.Status}: {run.ErrorCode} {run.ErrorMessage}");
        return run;
    }

    private Task<(int ExitCode, string Output)> RestoreAsync(string archive, params string[] options) =>
        TimeStoreServer.RunCommandAsync(
            ["restore", "--metadata", "--archive", archive, "--data-path", server.DataPath, .. options], _ct);

    /// <summary>The blob file whose content is <paramref name="content"/>.</summary>
    private string BlobHolding(string content) =>
        Directory.EnumerateFiles(Path.Combine(server.DataPath, FileSystemBlobStore.BlobsDirectory), "*", SearchOption.AllDirectories)
            .Single(file => File.ReadAllText(file) == content);

    private async Task PutAsync(IAmazonS3 s3, string key, string content) =>
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = content }, _ct);

    private async Task<string> ReadAsync(IAmazonS3 s3, string key)
    {
        using var response = await s3.GetObjectAsync(Bucket, key, _ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(_ct);
    }

    /// <summary>Keeps released blobs (so metadata backups stay restorable) and writes archives to the default folder.</summary>
    public sealed class RetainingServer : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment)
        {
            environment["TimeStore__Blobs__RetainDeletedDays"] = "1";
            environment["TimeStore__Backup__MinFreeBytes"] = "0";
            environment["TimeStore__Backup__Keep"] = "10";
        }
    }
}
