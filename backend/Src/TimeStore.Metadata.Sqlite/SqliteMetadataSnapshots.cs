using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// <see cref="IMetadataSnapshots"/> with <c>VACUUM INTO</c>: one read transaction copies the database as of its start
/// (instant T) into <c>tmp/backup-&lt;run id&gt;.db</c> next to the database, while the single writer keeps committing
/// (WAL). The copy is then trimmed (sessions, backup history), switched to a self-contained rollback journal and counted.
/// </summary>
public sealed class SqliteMetadataSnapshots(SqliteConnectionFactory connections) : IMetadataSnapshots
{
    public const string TempDirectory = "tmp";
    public const string FilePrefix = "backup-";
    public const string FileExtension = ".db";
    internal const string LeftoverPattern = FilePrefix + "*";
    private const string IdFormat = "N";
    private const string PathParameter = "$path";
    private const string Vacuum = "VACUUM INTO $path;";

    // Leave out what must not travel: admin session tokens (hashes, still) and the backup history, which describes
    // the source server (a restored server starts without runs, so none is left running).
    private const string Trim = """
        PRAGMA journal_mode = DELETE;
        DELETE FROM admin_sessions;
        DELETE FROM backup_runs;
        """;

    private const string Referenced = """
        SELECT blob_id, node_id, size, etag FROM objects WHERE blob_id IS NOT NULL
        UNION SELECT blob_id, node_id, size, etag FROM object_versions WHERE blob_id IS NOT NULL
        UNION SELECT blob_id, node_id, size, etag FROM object_parts
        UNION SELECT blob_id, node_id, size, etag FROM upload_parts
        """;

    private const string Count = """
        SELECT (SELECT COALESCE(MAX(version), 0) FROM schema_migrations),
               (SELECT COUNT(*) FROM buckets),
               (SELECT COUNT(*) FROM objects),
               (SELECT COUNT(*) FROM object_versions),
               COUNT(*), COALESCE(SUM(size), 0)
        FROM (
        """ + Referenced + ");";

    private const string List = Referenced + " ORDER BY blob_id;";

    /// <summary>Where snapshots of this database are written (also emptied of leftovers at startup).</summary>
    public static string DirectoryOf(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, TempDirectory);

    public async ValueTask<IMetadataSnapshot> CreateAsync(Guid runId, CancellationToken ct)
    {
        var directory = Directory.CreateDirectory(DirectoryOf(connections.DatabasePath));
        var path = Path.Combine(directory.FullName, FilePrefix + runId.ToString(IdFormat) + FileExtension);
        Snapshot.DeleteFiles(path); // a retried run id never exists, but never append to a stale file

        try
        {
            await using (var source = await connections.OpenAsync(ct))
            {
                await using var vacuum = source.CreateCommand();
                vacuum.CommandText = Vacuum;
                vacuum.Parameters.AddText(PathParameter, path);
                await vacuum.ExecuteNonQueryAsync(ct);
            }

            await using var copy = Snapshot.Open(path, SqliteOpenMode.ReadWrite);
            await copy.OpenAsync(ct);
            await using (var trim = copy.CreateCommand())
            {
                trim.CommandText = Trim;
                await trim.ExecuteNonQueryAsync(ct);
            }

            await using var count = copy.CreateCommand();
            count.CommandText = Count;
            await using var reader = await count.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var counts = (Schema: reader.GetInt32(0), Buckets: reader.GetInt64(1), Objects: reader.GetInt64(2),
                Versions: reader.GetInt64(3), Blobs: reader.GetInt64(4), Bytes: reader.GetInt64(5));
            await reader.DisposeAsync();
            await copy.CloseAsync();

            return new Snapshot(path, new MetadataSnapshotSummary(
                counts.Schema, counts.Buckets, counts.Objects, counts.Versions, counts.Blobs, counts.Bytes,
                new FileInfo(path).Length));
        }
        catch
        {
            Snapshot.DeleteFiles(path);
            throw;
        }
    }

    private sealed class Snapshot(string path, MetadataSnapshotSummary summary) : IMetadataSnapshot
    {
        private static readonly string[] Companions = ["-journal", "-wal", "-shm"];

        public MetadataSnapshotSummary Summary => summary;

        public async IAsyncEnumerable<BlobLocator> ListBlobsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await using var connection = Open(path, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = List;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                yield return new BlobLocator(reader.GetId(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
        }

        public Stream OpenRead() => new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        public ValueTask DisposeAsync()
        {
            DeleteFiles(path);
            return ValueTask.CompletedTask;
        }

        // Unpooled: the file is deleted when the run ends, so no connection may stay open on it.
        public static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());

        public static void DeleteFiles(string path)
        {
            File.Delete(path);
            foreach (var companion in Companions)
                File.Delete(path + companion);
        }
    }
}
