using TimeStore.Domain.Backups;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Domain.Uploads;

namespace TimeStore.Metadata.ContractTests;

/// <summary>
/// The behaviour every provider's <see cref="IBackupRunStore"/> and <see cref="IMetadataSnapshots"/> must have. A
/// provider gets a subclass that creates a fresh, empty store.
/// </summary>
public abstract class BackupStoreContract : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 18, 2, 0, 0, 123, DateTimeKind.Utc);

    protected IMetadataStore Store { get; private set; } = null!;

    protected IBackupRunStore Runs { get; private set; } = null!;

    protected IMetadataSnapshots Snapshots { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates an empty, initialized store (the three ports of one provider over one database).</summary>
    protected abstract ValueTask<(IMetadataStore Store, IBackupRunStore Runs, IMetadataSnapshots Snapshots)> CreateAsync();

    public virtual async ValueTask InitializeAsync() => (Store, Runs, Snapshots) = await CreateAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Runs ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindBackupRun_AfterCreate_ReturnsSameRun()
    {
        var run = Queued();
        Assert.True(await Runs.TryCreateBackupRunAsync(run, Ct));

        Assert.Equal(run, await Runs.FindBackupRunAsync(run.Id, Ct));
        Assert.Null(await Runs.FindBackupRunAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task TryCreateBackupRun_WhileAnotherIsActive_ReturnsFalse()
    {
        var first = Queued();
        Assert.True(await Runs.TryCreateBackupRunAsync(first, Ct));
        Assert.False(await Runs.TryCreateBackupRunAsync(Queued(), Ct));

        Assert.True(await Runs.TryStartBackupRunAsync(first.Id, "a.tar.zst", Now, Ct));
        Assert.False(await Runs.TryCreateBackupRunAsync(Queued(), Ct)); // running is active too

        Assert.True(await Runs.TryFinishBackupRunAsync(first.Id, Outcome(BackupStatus.Succeeded), Ct));
        Assert.True(await Runs.TryCreateBackupRunAsync(Queued(), Ct));
    }

    [Fact]
    public async Task TryCreateBackupRun_Concurrent_ExactlyOneWins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Runs.TryCreateBackupRunAsync(Queued(), Ct).AsTask()));

        Assert.Single(results, created => created);
    }

    [Fact]
    public async Task TryStartBackupRun_OnlyFromQueued()
    {
        var run = Queued();
        await Runs.TryCreateBackupRunAsync(run, Ct);

        Assert.True(await Runs.TryStartBackupRunAsync(run.Id, "timestore-node-1.tar.zst", Now, Ct));
        Assert.False(await Runs.TryStartBackupRunAsync(run.Id, "other.tar.zst", Now, Ct));
        Assert.False(await Runs.TryStartBackupRunAsync(Guid.CreateVersion7(), "x", Now, Ct));

        var started = await Runs.FindBackupRunAsync(run.Id, Ct);
        Assert.Equal((BackupStatus.Running, "timestore-node-1.tar.zst", Now), (started!.Status, started.ArchiveName, started.StartedAt));
    }

    [Fact]
    public async Task UpdateBackupProgress_RecordsSnapshotAndProgress_OnlyWhileRunning()
    {
        var run = Queued();
        await Runs.TryCreateBackupRunAsync(run, Ct);
        await Runs.UpdateBackupProgressAsync(run.Id, Now, new BackupProgress(9, 9, 9, 9), Ct);
        Assert.Equal(BackupProgress.None, (await Runs.FindBackupRunAsync(run.Id, Ct))!.Progress);

        await Runs.TryStartBackupRunAsync(run.Id, "a.tar", Now, Ct);
        var snapshotAt = Now.AddSeconds(1);
        await Runs.UpdateBackupProgressAsync(run.Id, snapshotAt, new BackupProgress(10, 4, 1000, 400), Ct);

        var found = await Runs.FindBackupRunAsync(run.Id, Ct);
        Assert.Equal(snapshotAt, found!.SnapshotAt);
        Assert.Equal(new BackupProgress(10, 4, 1000, 400), found.Progress);
    }

    [Fact]
    public async Task TryFinishBackupRun_RecordsOutcome_Once()
    {
        var run = Queued();
        await Runs.TryCreateBackupRunAsync(run, Ct);
        await Runs.TryStartBackupRunAsync(run.Id, "a.tar.zst", Now, Ct);
        var outcome = new BackupOutcome(BackupStatus.Failed, Now.AddMinutes(1), new BackupProgress(3, 1, 30, 10), null, null,
            "BACKUP_BLOB_CORRUPT", "Blob is corrupt.");

        Assert.True(await Runs.TryFinishBackupRunAsync(run.Id, outcome, Ct));
        Assert.False(await Runs.TryFinishBackupRunAsync(run.Id, Outcome(BackupStatus.Succeeded), Ct));

        var found = await Runs.FindBackupRunAsync(run.Id, Ct);
        Assert.Equal(
            (BackupStatus.Failed, Now.AddMinutes(1), new BackupProgress(3, 1, 30, 10), "BACKUP_BLOB_CORRUPT", "Blob is corrupt."),
            (found!.Status, found.FinishedAt, found.Progress, found.ErrorCode, found.ErrorMessage));
    }

    [Fact]
    public async Task TryFinishBackupRun_Succeeded_KeepsArchive()
    {
        var run = Queued();
        await Runs.TryCreateBackupRunAsync(run, Ct);
        await Runs.TryStartBackupRunAsync(run.Id, "a.tar.zst", Now, Ct);

        await Runs.TryFinishBackupRunAsync(run.Id, Outcome(BackupStatus.Succeeded) with { ArchiveSize = 1234, ArchiveSha256 = new string('a', 64) }, Ct);

        var found = await Runs.FindBackupRunAsync(run.Id, Ct);
        Assert.Equal((BackupStatus.Succeeded, 1234L, new string('a', 64)), (found!.Status, found.ArchiveSize, found.ArchiveSha256));
    }

    [Fact]
    public async Task TryFinishBackupRun_Queued_CanBeCancelled()
    {
        var run = Queued();
        await Runs.TryCreateBackupRunAsync(run, Ct);

        Assert.True(await Runs.TryFinishBackupRunAsync(run.Id, Outcome(BackupStatus.Cancelled), Ct));

        Assert.False(await Runs.TryStartBackupRunAsync(run.Id, "a.tar", Now, Ct));
        Assert.Equal(BackupStatus.Cancelled, (await Runs.FindBackupRunAsync(run.Id, Ct))!.Status);
    }

    [Fact]
    public async Task InterruptRunningBackupRuns_InterruptsRunningOnly()
    {
        var done = Queued();
        await Runs.TryCreateBackupRunAsync(done, Ct);
        await Runs.TryFinishBackupRunAsync(done.Id, Outcome(BackupStatus.Cancelled), Ct);
        var running = Queued();
        await Runs.TryCreateBackupRunAsync(running, Ct);
        await Runs.TryStartBackupRunAsync(running.Id, "a.tar", Now, Ct);

        Assert.Equal(1, await Runs.InterruptRunningBackupRunsAsync(Now.AddHours(1), "BACKUP_INTERRUPTED", "Stopped.", Ct));

        var interrupted = await Runs.FindBackupRunAsync(running.Id, Ct);
        Assert.Equal((BackupStatus.Interrupted, Now.AddHours(1), "BACKUP_INTERRUPTED", "Stopped."),
            (interrupted!.Status, interrupted.FinishedAt, interrupted.ErrorCode, interrupted.ErrorMessage));
        Assert.Equal(BackupStatus.Cancelled, (await Runs.FindBackupRunAsync(done.Id, Ct))!.Status);
        Assert.Equal(0, await Runs.InterruptRunningBackupRunsAsync(Now, "BACKUP_INTERRUPTED", "Stopped.", Ct));

        var queued = Queued();
        Assert.True(await Runs.TryCreateBackupRunAsync(queued, Ct)); // nothing active any more
        Assert.Equal(0, await Runs.InterruptRunningBackupRunsAsync(Now, "BACKUP_INTERRUPTED", "Stopped.", Ct));
        Assert.Equal([queued.Id], await Runs.ListQueuedBackupRunIdsAsync(Ct));
    }

    [Fact]
    public async Task ListBackupRuns_NewestFirst_UpToLimit()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            // Ids are UUIDv7: one millisecond apart at least, or their order within the millisecond is random.
            var run = Queued() with { Id = Guid.CreateVersion7(Now.AddSeconds(i)) };
            await Runs.TryCreateBackupRunAsync(run, Ct);
            await Runs.TryFinishBackupRunAsync(run.Id, Outcome(BackupStatus.Cancelled), Ct);
            ids.Add(run.Id);
        }

        Assert.Equal([ids[2], ids[1]], (await Runs.ListBackupRunsAsync(2, Ct)).Select(r => r.Id));
        Assert.Empty(await Runs.ListQueuedBackupRunIdsAsync(Ct));
    }

    // --- Snapshots -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_HoldsTheMetadataAtItsInstant()
    {
        var bucket = await CreateBucketAsync("photos");
        var kept = NewObject(bucket, "kept.txt", 10);
        var replaced = NewObject(bucket, "replaced.txt", 20);
        await Store.CommitObjectAsync(kept, CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(replaced, CommitCondition.Always, Ct);
        var upload = new MultipartUpload(Guid.CreateVersion7(), bucket.Id, "movie.mp4", "video/mp4", null, null, [], [], Now);
        Assert.True(await Store.TryCreateUploadAsync(upload, Ct));
        var part = new UploadPart(upload.UploadId, 1, NewBlob(5), null, Now);
        await Store.CommitPartAsync(part, Ct);

        await using var snapshot = await Snapshots.CreateAsync(Guid.CreateVersion7(), Ct);

        // After T: a new object and an overwrite; neither shows in the snapshot.
        await Store.CommitObjectAsync(NewObject(bucket, "late.txt", 99), CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(NewObject(bucket, "replaced.txt", 77), CommitCondition.Always, Ct);

        var blobs = new List<BlobLocator>();
        await foreach (var blob in snapshot.ListBlobsAsync(Ct))
            blobs.Add(blob);

        Assert.Equal(
            new[] { BlobOf(kept), BlobOf(replaced), part.Blob }.OrderBy(b => b.BlobId),
            blobs.OrderBy(b => b.BlobId));
        Assert.Equal(blobs.Count, blobs.DistinctBy(b => b.BlobId).Count());
        var summary = snapshot.Summary;
        Assert.Equal((1L, 2L, 3L, 35L), (summary.Buckets, summary.Objects, summary.Blobs, summary.Bytes));
        Assert.True(summary.SchemaVersion > 0);
        Assert.True(summary.DatabaseBytes > 0);
        await using var file = snapshot.OpenRead();
        Assert.Equal(summary.DatabaseBytes, file.Length);
    }

    [Fact]
    public async Task Snapshot_OfEmptyStore_HasNoBlobs()
    {
        await using var snapshot = await Snapshots.CreateAsync(Guid.CreateVersion7(), Ct);

        Assert.Equal((0L, 0L, 0L), (snapshot.Summary.Buckets, snapshot.Summary.Blobs, snapshot.Summary.Bytes));
        await foreach (var _ in snapshot.ListBlobsAsync(Ct))
            Assert.Fail("An empty store references no blob.");
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private static BackupRun Queued() =>
        BackupRun.Queue(BackupTriggers.Manual, BackupTargetTypes.Directory, BackupCompressions.Zstd, "admin", Now);

    private static BackupOutcome Outcome(BackupStatus status) =>
        new(status, Now.AddMinutes(1), BackupProgress.None, null, null, null, null);

    private async Task<Bucket> CreateBucketAsync(string name)
    {
        var bucket = new Bucket(Guid.CreateVersion7(), name, Now);
        Assert.True(await Store.TryCreateBucketAsync(bucket, Ct));
        return bucket;
    }

    // A single-blob object's ETag is its blob's MD5 (as ObjectService writes it).
    private static StoredObject NewObject(Bucket bucket, string key, long size)
    {
        var blob = NewBlob(size);
        return new StoredObject(bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(blob), blob.Md5Hex,
            StoredObject.DefaultContentType, null, null, null, [], [], Now);
    }

    private static BlobLocator NewBlob(long size) =>
        new(Guid.CreateVersion7(), "node-1", size, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()));

    private static BlobLocator BlobOf(StoredObject stored) => ((SingleBlob)stored.Data).Blob;
}
