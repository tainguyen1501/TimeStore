using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Backups;
using TimeStore.Application.Features.Auth;
using TimeStore.Contracts.Common;
using TimeStore.Domain.AccessKeys;
using TimeStore.Protocol.Auth;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// Backups end to end (stage B1): back up a running server through the admin API, restore the archive with the
/// <c>restore</c> command into a new data directory, start a server on it and read everything back over S3. Runs on the
/// AOT binary too (<c>TIMESTORE_SERVER_BINARY</c>).
/// </summary>
public sealed class BackupApiTests(BackupServer server) : IClassFixture<BackupServer>, IAsyncLifetime
{
    private const string Backups = "/api/v1/backups";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "timestore-e2e-archives", Guid.NewGuid().ToString("N"));
    private HttpClient _admin = null!; // the admin UI API (access keys): sign-in cookie
    private HttpClient _backup = null!; // the backup API: SigV4, root key

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_scratch);
        _admin = await server.AdminClientAsync();
        _backup = server.BackupClient();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _backup.Dispose();
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("zstd", "application/zstd", ".tar.zst")]
    [InlineData("none", "application/x-tar", ".tar")]
    public async Task StreamThenRestore_HoldsEverythingAtT_AndNothingAfter(string compression, string contentType, string extension)
    {
        using var s3 = server.Client();
        var bucket = $"round-{compression}";
        var versioned = $"versions-{compression}";
        await s3.PutBucketAsync(bucket, _ct);
        await PutAsync(s3, bucket, "kept.txt", "hello backup");
        await PutAsync(s3, bucket, "deleted.txt", "gone before T");
        await s3.DeleteObjectAsync(bucket, "deleted.txt", _ct);
        var large = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 123);
        await MultipartAsync(s3, bucket, "large.bin", large);
        await s3.PutBucketAsync(versioned, _ct);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = versioned,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, _ct);
        await PutAsync(s3, versioned, "doc.txt", "first");
        await PutAsync(s3, versioned, "doc.txt", "second");
        var key = await CreateAccessKeyAsync();

        var (archive, response) = await StreamAsync($"?compression={compression}&includeMasterKey=true");
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(extension, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        await PutAsync(s3, bucket, "late.txt", "after T"); // not in the archive

        await using var restored = new RestoredServer(archive);
        await restored.InitializeAsync();
        Assert.Contains("all checksums verified", restored.RestoreOutput);
        using var copy = restored.Client();

        Assert.Equal("hello backup", await ReadAsync(copy, bucket, "kept.txt"));
        Assert.Equal(SHA256.HashData(large), SHA256.HashData(await ReadBytesAsync(copy, bucket, "large.bin")));
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => copy.GetObjectAsync(bucket, "late.txt", _ct))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => copy.GetObjectAsync(bucket, "deleted.txt", _ct))).StatusCode);
        var versions = await copy.ListVersionsAsync(versioned, _ct);
        Assert.Equal(2, versions.Versions.Count(v => v.Key == "doc.txt"));
        Assert.Equal("second", await ReadAsync(copy, versioned, "doc.txt"));

        // The master key came along: an access key created before T still signs.
        using var keyed = restored.Client(key.AccessKeyId, key.SecretKey);
        Assert.Contains((await keyed.ListBucketsAsync(_ct)).Buckets, b => b.BucketName == bucket);

        // The streamed run is in the history of the source server.
        var runs = await ListAsync();
        Assert.Contains(runs, r => r is { Target: "stream", Status: BackupRunStatuses.Succeeded, Trigger: "api" } && r.Compression == compression);
    }

    [Fact]
    public async Task Backup_WhileObjectsAreOverwrittenAndDeleted_IsConsistent()
    {
        using var s3 = server.Client();
        const string bucket = "busy";
        await s3.PutBucketAsync(bucket, _ct);
        const int count = 120;
        for (var i = 0; i < count; i++)
            await PutAsync(s3, bucket, $"k{i:D3}", Payload(i, 0));

        // Overwrite and delete continuously; the server deletes released blobs at once (no delay), so every blob the
        // snapshot references but a later commit releases must be kept by the hold until the archive has it.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var writer = Task.Run(async () =>
        {
            for (var round = 1; !stop.IsCancellationRequested; round++)
            {
                for (var i = 0; i < count && !stop.IsCancellationRequested; i++)
                {
                    if ((i + round) % 5 == 0)
                        await s3.DeleteObjectAsync(bucket, $"k{i:D3}", CancellationToken.None);
                    else
                        await PutAsync(s3, bucket, $"k{i:D3}", Payload(i, round));
                }
            }
        }, CancellationToken.None);

        string archive;
        try
        {
            await Task.Delay(200, _ct);
            (archive, _) = await StreamAsync("?compression=zstd");
        }
        finally
        {
            await stop.CancelAsync();
            await writer;
        }

        await using var restored = new RestoredServer(archive);
        await restored.InitializeAsync();
        using var copy = restored.Client();
        var listed = await copy.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, _ct);
        Assert.NotEmpty(listed.S3Objects);
        foreach (var item in listed.S3Objects)
        {
            // Every object of the snapshot is whole: its bytes match the ETag recorded with it.
            var bytes = await ReadBytesAsync(copy, bucket, item.Key);
            Assert.Equal(item.ETag.Trim('"'), Convert.ToHexStringLower(MD5.HashData(bytes)));
        }
    }

    [Fact]
    public async Task DirectoryTarget_Start_Succeeds_Downloads_AndRefusesASecondCancel()
    {
        using var s3 = server.Client();
        await s3.PutBucketAsync("stored", _ct);
        await PutAsync(s3, "stored", "a.txt", "in the directory");

        var started = await _backup.PostAsJsonAsync(Backups, new StartBackupRequest(Compression: "none"), Json, _ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!;
        Assert.Equal(("manual", "directory", "none"), (run.Trigger, run.Target, run.Compression));
        Assert.Equal($"{Backups}/{run.Id}", started.Headers.Location?.ToString());

        var done = await WaitForAsync(run.Id);
        Assert.Equal(BackupRunStatuses.Succeeded, done.Status);
        Assert.NotNull(done.SnapshotAt);
        Assert.Equal(done.BlobsTotal, done.BlobsDone);
        var file = Path.Combine(server.BackupPath, done.ArchiveName!);
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(server.BackupPath, "*.partial"));

        using var download = await _backup.GetAsync($"{Backups}/{run.Id}/archive", HttpCompletionOption.ResponseHeadersRead, _ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/x-tar", download.Content.Headers.ContentType?.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync(_ct);
        Assert.Equal(await File.ReadAllBytesAsync(file, _ct), bytes);
        Assert.Equal(done.ArchiveSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(done.ArchiveSize, bytes.LongLength);

        Assert.Contains(await ListAsync(), r => r.Id == run.Id);
        await AssertErrorAsync(await _backup.PostAsync($"{Backups}/{run.Id}/cancel", null, _ct), HttpStatusCode.Conflict, BackupErrors.Codes.NotActive);
        await AssertErrorAsync(await _backup.GetAsync($"{Backups}/{Guid.CreateVersion7()}", _ct), HttpStatusCode.NotFound, BackupErrors.Codes.NotFound);
        await AssertErrorAsync(await _backup.GetAsync($"{Backups}/{Guid.CreateVersion7()}/archive", _ct), HttpStatusCode.NotFound, BackupErrors.Codes.NotFound);
    }

    [Fact]
    public async Task Start_WithBadInput_IsRefused()
    {
        await AssertErrorAsync(await _backup.PostAsJsonAsync(Backups, new StartBackupRequest(Compression: "gzip"), Json, _ct),
            HttpStatusCode.BadRequest, BackupErrors.Codes.InvalidCompression);
        await AssertErrorAsync(await _backup.PostAsJsonAsync(Backups, new StartBackupRequest(Target: "stream"), Json, _ct),
            HttpStatusCode.BadRequest, BackupErrors.Codes.InvalidTarget);
        await AssertErrorAsync(await _backup.GetAsync($"{Backups}/stream?compression=gzip", _ct),
            HttpStatusCode.BadRequest, BackupErrors.Codes.InvalidCompression);
    }

    [Fact]
    public async Task BackupApi_TakesSignedRequests_FromAdminAndBackupKeysOnly()
    {
        var viewer = await CreateAccessKeyAsync("viewer", AccessKeyRoles.Viewer);
        var backup = await CreateAccessKeyAsync("backup job", AccessKeyRoles.Backup);

        // A sign-in cookie, even an admin's, and an unsigned request are refused: the API is for programs.
        await AssertErrorAsync(await _admin.GetAsync(Backups, _ct), HttpStatusCode.Unauthorized, AuthErrors.Codes.SignatureRequired);
        await AssertErrorAsync(await _admin.PostAsJsonAsync(Backups, new StartBackupRequest(), Json, _ct),
            HttpStatusCode.Unauthorized, AuthErrors.Codes.SignatureRequired);
        await AssertErrorAsync(await server.AnonymousAdminClient().GetAsync($"{Backups}/stream", _ct),
            HttpStatusCode.Unauthorized, AuthErrors.Codes.SignatureRequired);

        // Signed by a viewer: the key is valid, the role is not.
        using (var signedByViewer = server.BackupClient(viewer.AccessKeyId, viewer.SecretKey))
            await AssertErrorAsync(await signedByViewer.GetAsync(Backups, _ct), HttpStatusCode.Forbidden, AuthErrors.Codes.Forbidden);

        // A wrong secret: the signature does not match.
        using (var wrongSecret = server.BackupClient(backup.AccessKeyId, "not-the-secret-key"))
            await AssertErrorAsync(await wrongSecret.GetAsync(Backups, _ct), HttpStatusCode.Forbidden, S3AuthErrors.Codes.SignatureMismatch);

        // A backup key lists and starts backups, never with the master key, and cannot sign in to the admin UI.
        using var signedByBackup = server.BackupClient(backup.AccessKeyId, backup.SecretKey);
        Assert.Equal(HttpStatusCode.OK, (await signedByBackup.GetAsync(Backups, _ct)).StatusCode);
        await AssertErrorAsync(await signedByBackup.PostAsJsonAsync(Backups, new StartBackupRequest(IncludeMasterKey: true), Json, _ct),
            HttpStatusCode.Forbidden, BackupErrors.Codes.MasterKeyForbidden);
        await AssertErrorAsync(await signedByBackup.GetAsync($"{Backups}/stream?includeMasterKey=true", _ct),
            HttpStatusCode.Forbidden, BackupErrors.Codes.MasterKeyForbidden);
        var started = await signedByBackup.PostAsJsonAsync(Backups, new StartBackupRequest(), Json, _ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!;
        Assert.Equal(backup.AccessKeyId, run.StartedBy);
        Assert.Equal(BackupRunStatuses.Succeeded, (await WaitForAsync(run.Id)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.AdminClientAsync(backup.AccessKeyId, backup.SecretKey));

        // A signed key reaches the backup API only: elsewhere it needs a session it cannot have.
        await AssertErrorAsync(await signedByBackup.GetAsync("/api/v1/buckets", _ct), HttpStatusCode.Unauthorized, AuthErrors.Codes.Unauthenticated);
    }

    [Fact]
    public async Task BackupApi_RefusesABodyThatDoesNotMatchItsSignedHash()
    {
        var signer = new SigV4SigningHandler(TimeStoreServer.AccessKey, TimeStoreServer.SecretKey, TimeStoreServer.Region,
            inner: new TamperingHandler());
        using var raw = new HttpClient(signer) { BaseAddress = new Uri(server.AdminUrl) };

        var response = await raw.PostAsJsonAsync(Backups, new StartBackupRequest(Compression: "none"), Json, _ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, AuthErrors.Codes.PayloadMismatch);
    }

    /// <summary>Changes the body after the signing handler hashed and signed it (asks for the master key instead).</summary>
    private sealed class TamperingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Content!.Headers.ToArray();
            request.Content = new StringContent("{\"includeMasterKey\":true}");
            request.Content.Headers.Clear();
            foreach (var header in headers)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content.Headers.ContentLength = null;
            return base.SendAsync(request, cancellationToken);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private async Task<(string Path, HttpResponseMessage Response)> StreamAsync(string query)
    {
        var response = await _backup.GetAsync($"{Backups}/stream{query}", HttpCompletionOption.ResponseHeadersRead, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var path = Path.Combine(_scratch, response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        await using (var file = File.Create(path))
            await (await response.Content.ReadAsStreamAsync(_ct)).CopyToAsync(file, _ct);
        return (path, response);
    }

    private async Task<BackupRunResponse> WaitForAsync(Guid runId)
    {
        BackupRunResponse? run = null;
        await TimeStoreServer.WaitUntilAsync(() =>
        {
            run = _backup.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{runId}", Json, _ct).GetAwaiter().GetResult();
            return run!.Status is not (BackupRunStatuses.Queued or BackupRunStatuses.Running);
        }, _ct);
        return run!;
    }

    private async Task<IReadOnlyList<BackupRunResponse>> ListAsync() =>
        (await _backup.GetFromJsonAsync<List<BackupRunResponse>>(Backups, Json, _ct))!;

    private async Task<CreatedAccessKeyResponse> CreateAccessKeyAsync(string description = "backup", string? role = null)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest(description, role), Json, _ct);
        return (await response.Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(Json, _ct))!;
    }

    private async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!.Code);
    }

    private static string Payload(int index, int round) => $"object {index} round {round} " + new string('x', 8 * 1024 + index);

    private Task PutAsync(IAmazonS3 s3, string bucket, string key, string body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body }, _ct);

    private async Task MultipartAsync(IAmazonS3 s3, string bucket, string key, byte[] data)
    {
        const int partSize = 5 * 1024 * 1024;
        var upload = await s3.InitiateMultipartUploadAsync(bucket, key, _ct);
        var parts = new List<PartETag>();
        for (int offset = 0, number = 1; offset < data.Length; offset += partSize, number++)
        {
            var part = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartNumber = number,
                InputStream = new MemoryStream(data, offset, Math.Min(partSize, data.Length - offset)),
            }, _ct);
            parts.Add(new PartETag(number, part.ETag));
        }

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartETags = parts,
        }, _ct);
    }

    private async Task<string> ReadAsync(IAmazonS3 s3, string bucket, string key) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(s3, bucket, key));

    private async Task<byte[]> ReadBytesAsync(IAmazonS3 s3, string bucket, string key)
    {
        using var response = await s3.GetObjectAsync(bucket, key, _ct);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, _ct);
        return buffer.ToArray();
    }
}

/// <summary>Failures that need a server of their own (they damage its data).</summary>
public sealed class BackupFailureTests(BackupServer server) : IClassFixture<BackupServer>
{
    private const string Backups = "/api/v1/backups";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task CorruptBlob_FailsTheRun_AndLeavesNoArchive()
    {
        using var s3 = server.Client();
        await s3.PutBucketAsync("damaged", _ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "damaged", Key = "a.txt", ContentBody = "original" }, _ct);
        var blob = Directory.EnumerateFiles(Path.Combine(server.DataPath, "blobs"), "*", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(blob, "ORIGINAL", _ct); // same size, other bytes: only the checksum tells

        using var admin = server.BackupClient();
        var started = await admin.PostAsJsonAsync(Backups, new StartBackupRequest(), Json, _ct);
        var run = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!;
        BackupRunResponse? done = null;
        await TimeStoreServer.WaitUntilAsync(() =>
        {
            done = admin.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{run.Id}", Json, _ct).GetAwaiter().GetResult();
            return done!.Status is not (BackupRunStatuses.Queued or BackupRunStatuses.Running);
        }, _ct);

        Assert.Equal((BackupRunStatuses.Failed, BackupErrors.Codes.BlobCorrupt), (done!.Status, done.ErrorCode));
        Assert.Empty(Directory.Exists(server.BackupPath) ? Directory.GetFiles(server.BackupPath) : []);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Backups}/{run.Id}/archive", _ct)).StatusCode);

        // Streamed, the same failure never yields a complete-looking archive: an error, or a cut connection.
        var streamed = await admin.GetAsync($"{Backups}/stream", HttpCompletionOption.ResponseHeadersRead, _ct);
        if (streamed.StatusCode == HttpStatusCode.OK)
            await Assert.ThrowsAnyAsync<Exception>(async () => await (await streamed.Content.ReadAsStreamAsync(_ct)).CopyToAsync(Stream.Null, _ct));
        else
            Assert.Equal(HttpStatusCode.InternalServerError, streamed.StatusCode);
    }
}

/// <summary>The restore command's refusals (no server needed).</summary>
public sealed class RestoreCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restore_IntoANonEmptyDirectory_IsRefused_AndTouchesNothing()
    {
        var directory = Directory.CreateTempSubdirectory("timestore-restore-");
        try
        {
            var archive = Path.Combine(directory.FullName, "x.tar");
            await File.WriteAllTextAsync(archive, "not checked: the directory is checked first", _ct);
            var data = Directory.CreateDirectory(Path.Combine(directory.FullName, "data"));
            await File.WriteAllTextAsync(Path.Combine(data.FullName, "keep.txt"), "mine", _ct);

            var (exitCode, output) = await TimeStoreServer.RunCommandAsync(["restore", "--archive", archive, "--data-path", data.FullName], _ct);

            Assert.Equal(1, exitCode);
            Assert.Contains(BackupErrors.Codes.RestoreNotEmpty, output);
            Assert.Equal(["keep.txt"], data.GetFiles().Select(f => f.Name));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Restore_OfAFileThatIsNoArchive_IsRefused_AndLeavesNoDataDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("timestore-restore-");
        try
        {
            var archive = Path.Combine(directory.FullName, "garbage.tar.zst");
            await File.WriteAllBytesAsync(archive, RandomNumberGenerator.GetBytes(4096), _ct);
            var data = Path.Combine(directory.FullName, "data");

            var (exitCode, output) = await TimeStoreServer.RunCommandAsync(["restore", "--archive", archive, "--data-path", data], _ct);

            Assert.Equal(1, exitCode);
            Assert.Contains(BackupErrors.Codes.RestoreInvalidArchive, output);
            Assert.False(Directory.Exists(data));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Restore_WithoutArchive_PrintsUsage()
    {
        var (exitCode, output) = await TimeStoreServer.RunCommandAsync(["restore"], _ct);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", output);
    }
}
