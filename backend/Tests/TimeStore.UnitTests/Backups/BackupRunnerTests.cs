using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Blob.FileSystem;
using TimeStore.Domain.Backups;
using TimeStore.Domain.Blobs;
using TimeStore.Infra.Backups;
using TimeStore.Infra.Services;
using TimeStore.Infra.Services.Backups;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Backups;

/// <summary>The backup engine over real blob files, fake runs and snapshots, and an in-memory target.</summary>
public sealed class BackupRunnerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-backup-runner", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly InMemoryBackupRunStore _runs = new();
    private readonly MemoryBackupTarget _target = new();
    private readonly TarBackupArchiveFormat _format = new();
    private FileSystemBlobStore _blobs = null!;
    private BlobCollector _deletions = null!;

    private string DataPath => Path.Combine(_root, "data");

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(DataPath, "node-1"));
        await _blobs.InitializeAsync(_ct);
        _deletions = new BlobCollector(new InMemoryMetadataStore(), _blobs, new BlobDeletionOptions(TimeSpan.FromMinutes(1)), TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _deletions.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Run_ArchivesEveryBlob_UnderAHold_AndTheArchiveRestores()
    {
        var blobs = new[] { await BlobAsync("one"), await BlobAsync("two two") };
        var snapshots = new FakeMetadataSnapshots(blobs) { OnCreate = () => Assert.True(_deletions.IsHeld) };
        var runner = Runner(snapshots);
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Null(execution!.Error);
        var done = execution.Run;
        Assert.Equal(BackupStatus.Succeeded, done.Status);
        Assert.Equal(new BackupProgress(2, 2, 10, 10), done.Progress);
        Assert.NotNull(done.SnapshotAt);
        Assert.StartsWith("timestore-node-1-", done.ArchiveName);
        Assert.EndsWith(".tar.zst", done.ArchiveName);
        var archive = _target.Archives[done.ArchiveName!];
        Assert.Equal(archive.LongLength, done.ArchiveSize);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archive)), done.ArchiveSha256);
        Assert.False(_deletions.IsHeld);
        Assert.Equal(0, snapshots.Open);

        var restored = Path.Combine(_root, "restored");
        var result = await Restore(archive, restored);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, result.Value.Manifest!.Blobs);
        Assert.False(result.Value.MasterKeyRestored);
        Assert.Equal(FakeMetadataSnapshots.Database, await File.ReadAllBytesAsync(Path.Combine(restored, "metadata.db"), _ct));
        foreach (var blob in blobs)
            Assert.Equal(await ReadAsync(blob), await File.ReadAllBytesAsync(Path.Combine(restored, BackupLayout.BlobEntry(blob.BlobId)), _ct));
    }

    [Fact]
    public async Task Run_WithAMissingBlob_Fails_AndCommitsNothing()
    {
        var gone = await BlobAsync("gone");
        await _blobs.DeleteAsync(gone.BlobId, _ct);
        var runner = Runner(new FakeMetadataSnapshots([await BlobAsync("ok"), gone]));
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal((BackupStatus.Failed, BackupErrors.Codes.BlobMissing), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.Equal(new BackupProgress(2, 1, 6, 2), execution.Run.Progress);
        Assert.Empty(_target.Archives);
        Assert.Equal(1, _target.Abandoned);
        Assert.False(_deletions.IsHeld);
    }

    [Theory]
    [InlineData("other bytes, same length")]
    [InlineData("short")]
    public async Task Run_WithADamagedBlob_FailsAsCorrupt(string damage)
    {
        var blob = await BlobAsync("other bytes, same lengtX");
        var damaged = Path.Combine(DataPath, BackupLayout.BlobEntry(blob.BlobId));
        await File.WriteAllTextAsync(damaged, damage, _ct);
        var runner = Runner(new FakeMetadataSnapshots([blob]));
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal((BackupStatus.Failed, BackupErrors.Codes.BlobCorrupt), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.Contains(blob.BlobId.ToString("N"), execution.Run.ErrorMessage);
        Assert.Empty(_target.Archives);
    }

    [Fact]
    public async Task Cancel_StopsARunningRun_AsCancelled()
    {
        var blobs = new[] { await BlobAsync("a"), await BlobAsync("b"), await BlobAsync("c") };
        var run = await QueueAsync();
        BackupRunner runner = null!;
        runner = Runner(new FakeMetadataSnapshots(blobs) { OnBlob = i => { if (i == 1) Assert.True(runner.Cancel(run.Id)); } });

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal((BackupStatus.Cancelled, BackupErrors.Codes.Cancelled), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.Empty(_target.Archives);
        Assert.False(runner.Cancel(run.Id)); // no longer running here
        Assert.False(_deletions.IsHeld);
    }

    [Fact]
    public async Task CallerGone_StopsTheRun_AsInterrupted()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var runner = Runner(new FakeMetadataSnapshots([await BlobAsync("a"), await BlobAsync("b")]) { OnBlob = i => { if (i == 1) caller.Cancel(); } });
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, caller.Token);

        Assert.Equal((BackupStatus.Interrupted, BackupErrors.Codes.Interrupted), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.NotNull(execution.Run.FinishedAt);
    }

    [Fact]
    public async Task Run_WithLittleFreeSpace_FailsBeforeTheSnapshot()
    {
        var snapshots = new FakeMetadataSnapshots([]);
        var runner = Runner(snapshots, minFreeBytes: long.MaxValue);
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal((BackupStatus.Failed, BackupErrors.Codes.LowDiskSpace), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.Equal(0, snapshots.Created);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task ASucceededRun_KeepsOnlyTheNewestArchives(int keep, int expected)
    {
        _target.Archives["timestore-node-1-20200101T000000Z.tar.zst"] = [1];
        _target.Archives["timestore-node-1-20200102T000000Z.tar.zst"] = [2];
        var runner = Runner(new FakeMetadataSnapshots([await BlobAsync("x")]), keep: keep);
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal(expected, _target.Archives.Count);
        Assert.Contains(execution!.Run.ArchiveName!, _target.Archives.Keys); // the new one always stays
        if (keep == 2)
            Assert.Contains("timestore-node-1-20200102T000000Z.tar.zst", _target.Archives.Keys); // then the next newest
    }

    [Fact]
    public async Task AFailedRun_DeletesNoArchive()
    {
        _target.Archives["timestore-node-1-20200101T000000Z.tar.zst"] = [1];
        var gone = await BlobAsync("gone");
        await _blobs.DeleteAsync(gone.BlobId, _ct);
        var runner = Runner(new FakeMetadataSnapshots([gone]), keep: 1);
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal(BackupStatus.Failed, execution!.Run.Status);
        Assert.Equal(["timestore-node-1-20200101T000000Z.tar.zst"], _target.Archives.Keys);
    }

    [Fact]
    public async Task ARun_ThatTheArchiveDirectoryCannotHold_IsRefused_BeforeWritingAnything()
    {
        _target.FreeBytes = 5; // the data is 10 bytes: an archive may be as large
        var snapshots = new FakeMetadataSnapshots([await BlobAsync("0123456789")]);
        var runner = Runner(snapshots);
        var run = await QueueAsync();

        var execution = await runner.RunAsync(run.Id, _target, _ct);

        Assert.Equal((BackupStatus.Failed, BackupErrors.Codes.LowDiskSpace), (execution!.Run.Status, execution.Run.ErrorCode));
        Assert.Contains("backup directory", execution.Run.ErrorMessage);
        Assert.Empty(_target.Archives);
        Assert.Equal(0, _target.Abandoned); // refused before an upload was opened
        Assert.False(_deletions.IsHeld);
    }

    [Fact]
    public async Task Run_OfARunThatIsNoLongerQueued_DoesNothing()
    {
        var runner = Runner(new FakeMetadataSnapshots([]));
        var run = await QueueAsync();
        await _runs.TryFinishBackupRunAsync(run.Id, new BackupOutcome(BackupStatus.Cancelled, DateTime.UtcNow, BackupProgress.None, null, null, null, null), _ct);

        Assert.Null(await runner.RunAsync(run.Id, _target, _ct));
        Assert.Null(await runner.RunAsync(Guid.CreateVersion7(), _target, _ct));
        Assert.Empty(_target.Archives);
    }

    [Fact]
    public async Task RunQueued_WithoutItsTarget_FailsAsNotConfigured()
    {
        var runner = new BackupRunner(_runs, new FakeMetadataSnapshots([]), _blobs, _deletions, _format, [], Options(), TimeProvider.System);
        var run = await QueueAsync();

        var execution = await runner.RunQueuedAsync(run.Id, _ct);

        Assert.Equal((BackupStatus.Failed, BackupErrors.Codes.TargetNotConfigured), (execution!.Run.Status, execution.Run.ErrorCode));
    }

    [Fact]
    public async Task Recover_InterruptsRunningAndQueuedStreams_ReturnsQueuedStoredRunsOldestFirst()
    {
        var now = DateTime.UtcNow;
        var running = BackupRun.Queue(BackupTriggers.Manual, BackupTargetTypes.Directory, BackupCompressions.Zstd, "root", now)
            with { Status = BackupStatus.Running };
        var stream = BackupRun.Queue(BackupTriggers.Api, BackupTargetTypes.Stream, BackupCompressions.Zstd, "root", now);
        var queued = BackupRun.Queue(BackupTriggers.Manual, BackupTargetTypes.Directory, BackupCompressions.Zstd, "root", now);
        foreach (var run in new[] { running, stream, queued })
            _runs.Seed(run);

        var wake = await Runner(new FakeMetadataSnapshots([])).RecoverAsync(_ct);

        Assert.Equal([queued.Id], wake);
        Assert.Equal(1, _target.IncompleteCleanups);
        Assert.Equal(BackupStatus.Interrupted, (await _runs.FindBackupRunAsync(running.Id, _ct))!.Status);
        Assert.Equal(BackupStatus.Interrupted, (await _runs.FindBackupRunAsync(stream.Id, _ct))!.Status);
        Assert.Equal(BackupStatus.Queued, (await _runs.FindBackupRunAsync(queued.Id, _ct))!.Status);
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private BackupRunner Runner(FakeMetadataSnapshots snapshots, long minFreeBytes = 0, int keep = BackupOptions.DefaultKeep) =>
        new(_runs, snapshots, _blobs, _deletions, _format, [_target], Options(minFreeBytes, keep), TimeProvider.System);

    private BackupOptions Options(long minFreeBytes = 0, int keep = BackupOptions.DefaultKeep) => new(
        "node-1", DataPath, Path.Combine(DataPath, "tmp"),
        BackupCompressions.Zstd, 3, minFreeBytes, keep);

    private async Task<BackupRun> QueueAsync()
    {
        var run = BackupRun.Queue(BackupTriggers.Manual, BackupTargetTypes.Directory, BackupCompressions.Zstd, "root", DateTime.UtcNow);
        Assert.True(await _runs.TryCreateBackupRunAsync(run, _ct));
        return run;
    }

    private async Task<BlobLocator> BlobAsync(string content) =>
        await _blobs.WriteAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), _ct);

    private async Task<byte[]> ReadAsync(BlobLocator blob)
    {
        await using var stream = await _blobs.OpenReadAsync(blob, _ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, _ct);
        return copy.ToArray();
    }

    private async Task<Primitives.Result<RestoreSummary>> Restore(byte[] archive, string dataPath)
    {
        var file = Path.Combine(_root, $"{Guid.NewGuid():N}.tar.zst");
        await File.WriteAllBytesAsync(file, archive, _ct);
        return await new BackupRestorer(_format).RestoreAsync(
            new RestoreRequest(file, dataPath, 10), _ct);
    }
}
