using Microsoft.Data.Sqlite;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Metadata.Sqlite.Schema;

/// <summary>Creates the database file, enables WAL and applies pending migrations, each in its own transaction.</summary>
public sealed class SqliteMetadataInitializer(SqliteConnectionFactory connections, TimeProvider clock) : IStorageInitializer
{
    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(connections.DatabasePath)) is { } directory)
            Directory.CreateDirectory(directory);
        DeleteLeftoverSnapshots();

        await using var connection = await connections.OpenAsync(ct);
        await ExecuteAsync(connection, null, SqliteMigrations.EnableWal, ct);
        await ExecuteAsync(connection, null, SqliteMigrations.CreateHistory, ct);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = SqliteMigrations.CurrentVersion;
        var current = (long)(await versionCommand.ExecuteScalarAsync(ct) ?? 0L);

        for (var version = current + 1; version <= SqliteMigrations.All.Length; version++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await ExecuteAsync(connection, transaction, SqliteMigrations.All[version - 1], ct);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = SqliteMigrations.RecordVersion;
            record.Parameters.AddInt64(SqliteMigrations.VersionParameter, version);
            record.Parameters.AddInt64(SqliteMigrations.AppliedAtParameter, SqliteTime.ToUnixMs(clock.UtcNowMs));
            await record.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>The newest schema version this build knows (a restore refuses archives with a newer one).</summary>
    public static int LatestVersion => SqliteMigrations.All.Length;

    // Single node: nothing runs a backup before startup completes, so snapshot files are from a crash.
    private void DeleteLeftoverSnapshots()
    {
        var snapshots = new DirectoryInfo(SqliteMetadataSnapshots.DirectoryOf(connections.DatabasePath));
        if (!snapshots.Exists)
            return;
        foreach (var leftover in snapshots.EnumerateFiles(SqliteMetadataSnapshots.LeftoverPattern))
            leftover.Delete();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
