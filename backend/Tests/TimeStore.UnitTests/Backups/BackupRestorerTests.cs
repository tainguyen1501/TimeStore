using System.Security.Cryptography;
using System.Text;
using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;
using TimeStore.Infra.Backups;

namespace TimeStore.UnitTests.Backups;

/// <summary>The restore's checks, on archives built entry by entry (good ones, and every way one can be wrong).</summary>
public sealed class BackupRestorerTests : IDisposable
{
    private static readonly byte[] Database = "metadata"u8.ToArray();
    private static readonly byte[] BlobBytes = "blob content"u8.ToArray();
    private static readonly Guid BlobId = Guid.CreateVersion7();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-restorer", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TarBackupArchiveFormat _format = new();

    private string DataPath => Path.Combine(_root, "data");

    public BackupRestorerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(BackupCompressions.Zstd)]
    [InlineData(BackupCompressions.None)]
    public async Task Restore_OfAGoodArchive_UnpacksTheDataDirectory(string compression)
    {
        var archive = await ArchiveAsync(compression);

        var result = await RestoreAsync(archive);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(compression, result.Value.Compression);
        Assert.Equal(Database, await File.ReadAllBytesAsync(Path.Combine(DataPath, "metadata.db"), _ct));
        Assert.Equal(BlobBytes, await File.ReadAllBytesAsync(Path.Combine(DataPath, BackupLayout.BlobEntry(BlobId)), _ct));
        Assert.False(Directory.Exists(Path.Combine(DataPath, "tmp")) && Directory.GetFiles(Path.Combine(DataPath, "tmp")).Length > 0);
    }

    [Fact]
    public async Task Restore_IntoAnExistingEmptyDirectory_Works()
    {
        Directory.CreateDirectory(DataPath);

        Assert.True((await RestoreAsync(await ArchiveAsync())).IsSuccess);
    }

    [Fact]
    public async Task Restore_IntoAVolumeWithOnlyLostAndFound_Works_AndAFailureKeepsIt()
    {
        var lostAndFound = Directory.CreateDirectory(Path.Combine(DataPath, "lost+found"));

        Assert.True((await RestoreAsync(await ArchiveAsync())).IsSuccess);
        Assert.True(lostAndFound.Exists);

        Directory.Delete(DataPath, recursive: true);
        lostAndFound.Create();
        await AssertRefusedAsync(await ArchiveAsync(withChecksums: false), BackupErrors.Codes.RestoreInvalidArchive, keepsDirectory: true);
        Assert.Equal(["lost+found"], Directory.GetFileSystemEntries(DataPath).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Restore_IntoANonEmptyDirectory_IsRefused()
    {
        Directory.CreateDirectory(DataPath);
        await File.WriteAllTextAsync(Path.Combine(DataPath, "mine"), "x", _ct);

        await AssertRefusedAsync(await ArchiveAsync(), BackupErrors.Codes.RestoreNotEmpty, keepsDirectory: true);
        Assert.Equal(["mine"], Directory.GetFiles(DataPath).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Restore_OfANewerFormat_IsRefused() =>
        await AssertRefusedAsync(await ArchiveAsync(manifest: m => m with { Format = BackupLayout.FormatVersion + 1 }), BackupErrors.Codes.RestoreUnsupported);

    [Fact]
    public async Task Restore_OfANewerSchema_IsRefused() =>
        await AssertRefusedAsync(await ArchiveAsync(manifest: m => m with { SchemaVersion = 11 }), BackupErrors.Codes.RestoreUnsupported);

    [Fact]
    public async Task Restore_WithADamagedBlob_IsRefused_AndRemovesWhatItWrote()
    {
        var archive = await ArchiveAsync(blob: "damaged blob"u8.ToArray(), blobListedAs: BlobBytes);

        await AssertRefusedAsync(archive, BackupErrors.Codes.RestoreChecksumMismatch);
    }

    [Fact]
    public async Task Restore_WithAWrongMetadataChecksum_IsRefused() =>
        await AssertRefusedAsync(await ArchiveAsync(manifest: m => m with { MetadataSha256 = new string('0', 64) }),
            BackupErrors.Codes.RestoreChecksumMismatch);

    [Theory]
    [InlineData("../escape")]
    [InlineData("blobs/../../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("notes.txt")]
    public async Task Restore_WithAnUnexpectedEntry_IsRefused_AndWritesNothingOutside(string name)
    {
        var archive = await ArchiveAsync(extra: (name, "evil"u8.ToArray()));

        await AssertRefusedAsync(archive, BackupErrors.Codes.RestoreInvalidArchive);
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public async Task Restore_WithoutTheChecksumList_IsRefused() =>
        await AssertRefusedAsync(await ArchiveAsync(withChecksums: false), BackupErrors.Codes.RestoreInvalidArchive);

    [Fact]
    public async Task Restore_OfATruncatedArchive_IsRefused()
    {
        var archive = await ArchiveAsync(BackupCompressions.Zstd, blob: RandomNumberGenerator.GetBytes(64 * 1024));
        await File.WriteAllBytesAsync(archive, (await File.ReadAllBytesAsync(archive, _ct))[..^100], _ct);

        await AssertRefusedAsync(archive, BackupErrors.Codes.RestoreInvalidArchive);
    }

    [Fact]
    public async Task Restore_WhenTheMasterKeyFileExists_IsRefused()
    {
        var key = Path.Combine(_root, "existing.key");
        await File.WriteAllTextAsync(key, "keep me", _ct);

        var result = await RestoreAsync(await ArchiveAsync(masterKey: [9, 9]), key);

        Assert.Equal(BackupErrors.Codes.RestoreMasterKeyExists, result.Error.Code);
        Assert.Equal("keep me", await File.ReadAllTextAsync(key, _ct));
    }

    [Fact]
    public async Task IfEmpty_ADirectoryWithData_IsLeftAlone_WithoutReadingTheArchive()
    {
        Directory.CreateDirectory(DataPath);
        await File.WriteAllTextAsync(Path.Combine(DataPath, "metadata.db"), "live data", _ct);

        var result = await RestoreAsync(Path.Combine(_root, "never-read.tar.zst"), ifEmpty: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WasSkipped);
        Assert.Equal("live data", await File.ReadAllTextAsync(Path.Combine(DataPath, "metadata.db"), _ct));
    }

    [Fact]
    public async Task IfEmpty_AnEmptyDirectory_IsRestored_AndLeavesNoMarker()
    {
        Directory.CreateDirectory(Path.Combine(DataPath, "lost+found"));

        var result = await RestoreAsync(await ArchiveAsync(), ifEmpty: true);

        Assert.False(result.Value.WasSkipped);
        Assert.Equal(Database, await File.ReadAllBytesAsync(Path.Combine(DataPath, "metadata.db"), _ct));
        Assert.False(File.Exists(Path.Combine(DataPath, BackupRestorer.InProgressMarker)));
    }

    [Fact]
    public async Task AnInterruptedRestore_IsStartedOver_EvenWithIfEmpty()
    {
        // What a process killed half way leaves: the marker and part of the data.
        Directory.CreateDirectory(Path.Combine(DataPath, "blobs", "aa"));
        await File.WriteAllTextAsync(Path.Combine(DataPath, BackupRestorer.InProgressMarker), "", _ct);
        await File.WriteAllTextAsync(Path.Combine(DataPath, "metadata.db"), "half written", _ct);
        await File.WriteAllTextAsync(Path.Combine(DataPath, "blobs", "aa", "partial"), "x", _ct);

        var result = await RestoreAsync(await ArchiveAsync(), ifEmpty: true);

        Assert.False(result.Value.WasSkipped);
        Assert.Equal(Database, await File.ReadAllBytesAsync(Path.Combine(DataPath, "metadata.db"), _ct));
        Assert.False(File.Exists(Path.Combine(DataPath, "blobs", "aa", "partial")));
        Assert.False(File.Exists(Path.Combine(DataPath, BackupRestorer.InProgressMarker)));
    }

    [Fact]
    public async Task AFailedRestore_LeavesNoMarker()
    {
        Directory.CreateDirectory(DataPath);

        await AssertRefusedAsync(await ArchiveAsync(withChecksums: false), BackupErrors.Codes.RestoreInvalidArchive, keepsDirectory: true);

        Assert.Empty(Directory.GetFileSystemEntries(DataPath));
    }

    [Fact]
    public async Task Restore_FromAUrl_ReadsTheArchiveAsItDownloads()
    {
        var archive = await ArchiveAsync();
        await using var served = await ServedFile.StartAsync(archive, _ct);

        var result = await RestoreAsync(served.Url + "?X-Amz-Signature=secret-part");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(BlobBytes, await File.ReadAllBytesAsync(Path.Combine(DataPath, BackupLayout.BlobEntry(BlobId)), _ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_OfAnArchiveThatCannotBeRead_IsRefused_WithoutTheUrlsQuery(bool url)
    {
        await using var served = await ServedFile.StartAsync(Path.Combine(_root, "missing.tar.zst"), _ct);
        var archive = url ? served.Url.Replace("missing", "absent") + "?X-Amz-Signature=secret-part" : Path.Combine(_root, "absent.tar.zst");

        var result = await RestoreAsync(archive);

        Assert.Equal(BackupErrors.Codes.RestoreArchiveUnavailable, result.Error.Code);
        Assert.DoesNotContain("secret-part", result.Error.Message);
        Assert.Contains(url ? "404" : "does not exist", result.Error.Message);
        Assert.False(Directory.Exists(DataPath));
    }

    /// <summary>Serves one file over HTTP on a free loopback port (404 for any other path).</summary>
    private sealed class ServedFile : IAsyncDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _loop;

        private ServedFile(string path, int port)
        {
            Url = $"http://127.0.0.1:{port}/{Path.GetFileName(path)}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return; // stopped
                    }

                    if (context.Request.Url!.AbsolutePath == "/" + Path.GetFileName(path) && File.Exists(path))
                    {
                        await using var file = File.OpenRead(path);
                        await file.CopyToAsync(context.Response.OutputStream);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }

                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public static Task<ServedFile> StartAsync(string path, CancellationToken ct)
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return Task.FromResult(new ServedFile(path, port));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _loop;
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private async Task AssertRefusedAsync(string archive, string code, bool keepsDirectory = false)
    {
        var result = await RestoreAsync(archive);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
        if (!keepsDirectory)
            Assert.False(Directory.Exists(DataPath)); // created by the restore, removed again
    }

    private ValueTask<Primitives.Result<RestoreSummary>> RestoreAsync(string archive, string? masterKeyPath = null, bool ifEmpty = false) =>
        new BackupRestorer(_format).RestoreAsync(
            new RestoreRequest(archive, DataPath, masterKeyPath ?? Path.Combine(DataPath, "master.key"), 10, ifEmpty), _ct);

    /// <summary>Writes an archive the way the engine does, with knobs to break it.</summary>
    private async Task<string> ArchiveAsync(
        string compression = BackupCompressions.Zstd,
        Func<BackupManifest, BackupManifest>? manifest = null,
        byte[]? blob = null,
        byte[]? blobListedAs = null,
        (string Name, byte[] Content)? extra = null,
        bool withChecksums = true,
        byte[]? masterKey = null)
    {
        blob ??= BlobBytes;
        var listed = blobListedAs ?? blob; // what blobs.sha256 claims the blob holds
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}{_format.ExtensionOf(compression)}");
        var built = new BackupManifest(BackupLayout.FormatVersion, "test", "node-1", DateTime.UtcNow, 10, compression, 1, 1, 1, 1,
            blob.Length, Database.Length, Sha256(Database), masterKey is not null);
        built = manifest?.Invoke(built) ?? built;

        await using var file = File.Create(path);
        await using var writer = _format.CreateWriter(file, compression, 3);
        var checksums = new StringBuilder();
        var manifestBytes = _format.SerializeManifest(built);
        await writer.AddAsync(BackupLayout.ManifestEntry, new MemoryStream(manifestBytes), manifestBytes.Length, _ct);
        await writer.AddAsync(BackupLayout.MetadataEntry, new MemoryStream(Database), Database.Length, _ct);
        checksums.Append(BackupLayout.ChecksumLine(Sha256(Database), BackupLayout.MetadataEntry));
        var blobEntry = BackupLayout.BlobEntry(BlobId);
        await writer.AddAsync(blobEntry, new MemoryStream(blob), blob.Length, _ct);
        checksums.Append(BackupLayout.ChecksumLine(Sha256(listed), blobEntry));
        if (extra is { } entry)
            await writer.AddAsync(entry.Name, new MemoryStream(entry.Content), entry.Content.Length, _ct);
        if (masterKey is not null)
        {
            await writer.AddAsync(BackupLayout.MasterKeyEntry, new MemoryStream(masterKey), masterKey.Length, _ct);
            checksums.Append(BackupLayout.ChecksumLine(Sha256(masterKey), BackupLayout.MasterKeyEntry));
        }

        if (withChecksums)
        {
            var list = Encoding.ASCII.GetBytes(checksums.ToString());
            await writer.AddAsync(BackupLayout.ChecksumsEntry, new MemoryStream(list), list.Length, _ct);
        }

        await writer.CompleteAsync(_ct);
        return path;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
