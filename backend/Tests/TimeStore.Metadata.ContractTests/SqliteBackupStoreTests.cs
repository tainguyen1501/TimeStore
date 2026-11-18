using Microsoft.Data.Sqlite;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Stores;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Metadata.ContractTests;

/// <summary>The backup contract on SQLite, plus what only the SQLite snapshot does (file handling, trimming).</summary>
public sealed class SqliteBackupStoreTests : BackupStoreContract
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _connections = null!;

    private string SnapshotDirectory => SqliteMetadataSnapshots.DirectoryOf(_connections.DatabasePath);

    protected override async ValueTask<(IMetadataStore Store, IBackupRunStore Runs, IMetadataSnapshots Snapshots)> CreateAsync()
    {
        _connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        await new SqliteMetadataInitializer(_connections, TimeProvider.System).InitializeAsync(Ct);
        var store = new SqliteMetadataStore(_connections);
        return (store, store, new SqliteMetadataSnapshots(_connections));
    }

    public override async ValueTask DisposeAsync()
    {
        if (Store is IAsyncDisposable store)
            await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Snapshot_LeavesOutSessionsAndBackupHistory_AndIsSelfContained()
    {
        await Store.CreateSessionAsync(new AdminSession([1, 2, 3], "root", DateTime.UtcNow, DateTime.UtcNow.AddHours(1)), Ct);
        var run = Domain.Backups.BackupRun.Queue("manual", "stream", "zstd", false, "root", DateTime.UtcNow);
        Assert.True(await Runs.TryCreateBackupRunAsync(run, Ct));

        await using var snapshot = await Snapshots.CreateAsync(run.Id, Ct);
        var copy = Path.Combine(_directory, "copy.db");
        await using (var source = snapshot.OpenRead())
        await using (var target = File.Create(copy))
            await source.CopyToAsync(target, Ct);

        await using var connection = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(Ct);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM admin_sessions;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM backup_runs;"));
        Assert.Equal("delete", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, (await Store.FindSessionAsync([1, 2, 3], Ct)) is null ? 0L : 1L); // the live database keeps them
    }

    [Fact]
    public async Task Snapshot_Dispose_DeletesItsFile()
    {
        var snapshot = await Snapshots.CreateAsync(Guid.CreateVersion7(), Ct);
        Assert.Single(Directory.GetFiles(SnapshotDirectory));

        await snapshot.DisposeAsync();

        Assert.Empty(Directory.GetFiles(SnapshotDirectory));
    }

    [Fact]
    public async Task Initialize_DeletesLeftoverSnapshots_KeepsOtherFiles()
    {
        Directory.CreateDirectory(SnapshotDirectory);
        var leftover = Path.Combine(SnapshotDirectory, "backup-0123.db");
        var journal = leftover + "-journal";
        var other = Path.Combine(SnapshotDirectory, "0123.tmp-other");
        foreach (var file in new[] { leftover, journal, other })
            await File.WriteAllTextAsync(file, "x", Ct);

        await new SqliteMetadataInitializer(_connections, TimeProvider.System).InitializeAsync(Ct);

        Assert.Equal([other], Directory.GetFiles(SnapshotDirectory));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
