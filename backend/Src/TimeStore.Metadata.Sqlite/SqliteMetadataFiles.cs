using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// <see cref="IMetadataFiles"/> on SQLite: opens a database file read-only, without a pool, and lists every blob it
/// references with the bucket and key using it. Joins go through primary keys (parts by version and upload id), so a
/// database of millions of objects is read in one pass.
/// </summary>
public sealed class SqliteMetadataFiles : IMetadataFiles
{
    // Single-blob objects and history rows (multipart ones have no blob_id), the parts of multipart objects (current and
    // history), and the parts of open uploads. Column order: blob_id, size, bucket name, key.
    private const string Referenced = """
        SELECT o.blob_id, o.size, b.name, o.key FROM objects o JOIN buckets b ON b.id = o.bucket_id
            WHERE o.blob_id IS NOT NULL
        UNION ALL
        SELECT p.blob_id, p.size, b.name, o.key FROM objects o JOIN object_parts p ON p.version_id = o.version_id
            JOIN buckets b ON b.id = o.bucket_id
        UNION ALL
        SELECT v.blob_id, v.size, b.name, v.key FROM object_versions v JOIN buckets b ON b.id = v.bucket_id
            WHERE v.blob_id IS NOT NULL
        UNION ALL
        SELECT p.blob_id, p.size, b.name, v.key FROM object_versions v JOIN object_parts p ON p.version_id = v.version_id
            JOIN buckets b ON b.id = v.bucket_id
        UNION ALL
        SELECT p.blob_id, p.size, b.name, u.key FROM multipart_uploads u JOIN upload_parts p ON p.upload_id = u.upload_id
            JOIN buckets b ON b.id = u.bucket_id;
        """;

    private const string HasRules = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'replication_rules';";

    private const string Rules = """
        SELECT b.name, r.endpoint, r.target_bucket, r.prefix, r.replicate_deletes
        FROM replication_rules r JOIN buckets b ON b.id = r.bucket_id ORDER BY b.name;
        """;

    public async IAsyncEnumerable<ReferencedBlob> ListReferencedBlobsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenAsync(path, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = Referenced;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return new ReferencedBlob(reader.GetId(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3));
    }

    public async ValueTask<IReadOnlyList<ReplicationRuleSummary>> ListReplicationRulesAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenAsync(path, ct);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = HasRules;
            if (await exists.ExecuteScalarAsync(ct) is null)
                return []; // a schema from before replication (v12)
        }

        await using var command = connection.CreateCommand();
        command.CommandText = Rules;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rules = new List<ReplicationRuleSummary>();
        while (await reader.ReadAsync(ct))
            rules.Add(new ReplicationRuleSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0));
        return rules;
    }

    // Read-only and unpooled: the file is closed again at once (a restore moves it next).
    private static async ValueTask<SqliteConnection> OpenAsync(string path, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
