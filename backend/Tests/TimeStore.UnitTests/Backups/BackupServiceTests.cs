using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.Backups;
using TimeStore.Domain.Backups;
using TimeStore.Infra.Backups;
using TimeStore.Infra.Services;
using TimeStore.Infra.Services.Backups;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Backups;

/// <summary>The one flow: validate, insert the run, then queue it (stored targets); refusals and cancel.</summary>
public sealed class BackupServiceTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly InMemoryBackupRunStore _runs = new();
    private readonly BackupQueue _queue = new();
    private readonly MemoryBackupTarget _directory = new();
    private readonly BlobCollector _deletions;
    private readonly BackupRunner _runner;

    private static readonly BackupOptions Options = new("node-1", ".", "tmp", BackupCompressions.Zstd, 3, 0);
    private static readonly BackupCaller Admin = new("root");
    private static readonly BackupCaller BackupKey = new("backup-job");

    public BackupServiceTests()
    {
        var blobs = new RecordingBlobStore();
        _deletions = new BlobCollector(new InMemoryMetadataStore(), blobs, new Application.Abstractions.BlobDeletionOptions(TimeSpan.Zero), TimeProvider.System);
        _runner = new BackupRunner(_runs, new FakeMetadataSnapshots([]), blobs, _deletions, new TarBackupArchiveFormat(),
            [_directory], Options, TimeProvider.System);
    }

    public ValueTask DisposeAsync() => _deletions.DisposeAsync();

    private BackupService Service(bool withDirectory = true) =>
        new(_runs, _runner, _queue, withDirectory ? [_directory] : [], Options, TimeProvider.System);

    [Fact]
    public async Task Start_QueuesARun_AndWakesTheWorker()
    {
        var result = await Service().StartAsync(new StartBackupRequest(), Admin, _ct);

        var run = result.Value;
        Assert.Equal((BackupRunStatuses.Queued, "manual", "directory", "zstd", "root"),
            (run.Status, run.Trigger, run.Target, run.Compression, run.StartedBy));
        await foreach (var queued in _queue.ReadAllAsync(_ct))
        {
            Assert.Equal(run.Id, queued);
            break;
        }
    }

    [Fact]
    public async Task Start_WhileOneIsActive_IsRefused()
    {
        var service = Service();
        await service.StartAsync(new StartBackupRequest(), Admin, _ct);

        Assert.Equal(BackupErrors.Codes.Active, (await service.StartAsync(new StartBackupRequest(), Admin, _ct)).Error.Code);
    }

    [Theory]
    [InlineData(null, "gzip", BackupErrors.Codes.InvalidCompression)]
    [InlineData("stream", null, BackupErrors.Codes.InvalidTarget)]
    [InlineData("s3", null, BackupErrors.Codes.InvalidTarget)]
    public async Task Start_WithBadInput_IsRefused(string? target, string? compression, string code) =>
        Assert.Equal(code, (await Service().StartAsync(new StartBackupRequest(target, compression), Admin, _ct)).Error.Code);

    [Fact]
    public async Task Start_WithoutABackupDirectory_SaysSo() =>
        Assert.Equal(BackupErrors.Codes.TargetNotConfigured,
            (await Service(withDirectory: false).StartAsync(new StartBackupRequest(), Admin, _ct)).Error.Code);

    [Fact]
    public async Task Cancel_AQueuedRun_EndsIt_AndTheWorkerSkipsIt()
    {
        var service = Service();
        var run = (await service.StartAsync(new StartBackupRequest(), Admin, _ct)).Value;

        var cancelled = await service.CancelAsync(run.Id, _ct);

        Assert.Equal((BackupRunStatuses.Cancelled, BackupErrors.Codes.Cancelled), (cancelled.Value.Status, cancelled.Value.ErrorCode));
        Assert.Null(await _runner.RunQueuedAsync(run.Id, _ct));
        Assert.Equal(BackupErrors.Codes.NotActive, (await service.CancelAsync(run.Id, _ct)).Error.Code);
        Assert.Equal(BackupErrors.Codes.NotFound, (await service.CancelAsync(Guid.CreateVersion7(), _ct)).Error.Code);
    }

    [Fact]
    public async Task OpenArchive_OnlyForASucceededRunWithAStoredArchive()
    {
        var service = Service();
        var run = (await service.StartAsync(new StartBackupRequest(), Admin, _ct)).Value;
        Assert.Equal(BackupErrors.Codes.ArchiveNotFound, (await service.OpenArchiveAsync(run.Id, _ct)).Error.Code);

        await _runner.RunQueuedAsync(run.Id, _ct);
        var download = await service.OpenArchiveAsync(run.Id, _ct);
        await using (download.Value.Content)
            Assert.Equal((await service.GetAsync(run.Id, _ct)).Value.ArchiveName, download.Value.Name);

        _directory.Archives.Clear(); // removed from the directory by hand
        Assert.Equal(BackupErrors.Codes.ArchiveNotFound, (await service.OpenArchiveAsync(run.Id, _ct)).Error.Code);
        Assert.Equal(BackupErrors.Codes.NotFound, (await service.OpenArchiveAsync(Guid.CreateVersion7(), _ct)).Error.Code);
    }

    [Fact]
    public async Task DeleteArchive_FreesTheFile_AndKeepsTheRun()
    {
        var service = Service();
        var run = (await service.StartAsync(new StartBackupRequest(), Admin, _ct)).Value;
        Assert.Equal(BackupErrors.Codes.ArchiveNotFound, (await service.DeleteArchiveAsync(run.Id, _ct)).Error.Code); // not run yet
        await _runner.RunQueuedAsync(run.Id, _ct);

        var deleted = await service.DeleteArchiveAsync(run.Id, _ct);

        Assert.True(deleted.IsSuccess);
        Assert.Empty(_directory.Archives);
        Assert.Equal(BackupRunStatuses.Succeeded, (await service.GetAsync(run.Id, _ct)).Value.Status);
        Assert.Equal(BackupErrors.Codes.ArchiveNotFound, (await service.DeleteArchiveAsync(run.Id, _ct)).Error.Code);
        Assert.Equal(BackupErrors.Codes.ArchiveNotFound, (await service.OpenArchiveAsync(run.Id, _ct)).Error.Code);
        Assert.Equal(BackupErrors.Codes.NotFound, (await service.DeleteArchiveAsync(Guid.CreateVersion7(), _ct)).Error.Code);
    }

    [Fact]
    public async Task Stream_RunsInline_IntoTheGivenTarget()
    {
        var target = new MemoryBackupTarget(BackupTargetTypes.Stream);

        var (result, exception) = await Service().StreamAsync(BackupCompressions.None, Admin, target, _ct);

        Assert.Null(exception);
        Assert.Equal((BackupRunStatuses.Succeeded, "api", "stream"), (result.Value.Status, result.Value.Trigger, result.Value.Target));
        Assert.EndsWith(".tar", Assert.Single(target.Archives).Key);
        Assert.Contains(await Service().ListAsync(_ct), r => r.Id == result.Value.Id);
    }

    [Fact]
    public async Task ABackupKey_StartsBackups_UnderItsOwnName()
    {
        var started = await Service().StartAsync(new StartBackupRequest(), BackupKey, _ct);

        Assert.Equal("backup-job", started.Value.StartedBy);
    }
}
