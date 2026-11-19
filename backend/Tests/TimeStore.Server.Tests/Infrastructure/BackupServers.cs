namespace TimeStore.Server.Tests.Infrastructure;

/// <summary>
/// A server with a backup directory and no delay before released blobs are deleted: without the blob hold an
/// overwritten blob would vanish at once, so a backup taken during writes proves the hold works.
/// </summary>
public class BackupServer : TimeStoreServer
{
    public string BackupPath { get; } =
        Path.Combine(Path.GetTempPath(), "timestore-e2e-backups", Guid.NewGuid().ToString("N"));

    protected override int BlobDeleteDelaySeconds => 0;

    protected override void Configure(IDictionary<string, string?> environment)
    {
        environment["TimeStore__Backup__Path"] = BackupPath;
        environment["TimeStore__Backup__MinFreeBytes"] = "0";
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(BackupPath))
            Directory.Delete(BackupPath, recursive: true);
    }
}

/// <summary>A server started on the data directory that <c>TimeStore.Server restore</c> made from an archive.</summary>
/// <param name="archive">A file or an http(s) URL.</param>
/// <param name="options">More options of the restore command (<c>--if-empty</c>).</param>
public sealed class RestoredServer(string archive, params string[] options) : TimeStoreServer
{
    public string RestoreOutput { get; private set; } = string.Empty;

    protected override async ValueTask PrepareDataAsync(string dataPath)
    {
        var (exitCode, output) = await RunCommandAsync(
            ["restore", "--archive", archive, "--data-path", dataPath, .. options], CancellationToken.None);
        RestoreOutput = output;
        if (exitCode != 0)
            throw new InvalidOperationException($"restore exited with {exitCode}:\n{output}");
    }
}
