using System.Text;
using Microsoft.Data.Sqlite;
using TimeStore.Domain.Blobs;
using TimeStore.Primitives;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    // Well below SQLite's bound-parameter limit (32 766); one index probe per id (ix_objects_blob).
    private const int BlobBatchSize = 500;
    private const string BlobParameterPrefix = "$b";

    public async ValueTask<IReadOnlyList<Guid>> FindUnreferencedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct)
    {
        if (blobIds.Count == 0)
            return [];

        var referenced = new HashSet<Guid>();
        await using var connection = await connections.OpenAsync(ct);
        for (var start = 0; start < blobIds.Count; start += BlobBatchSize)
        {
            var count = Math.Min(BlobBatchSize, blobIds.Count - start);
            await using var command = connection.CreateCommand();
            var list = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var name = BlobParameterPrefix + i; // parameter names only: no value ever enters the SQL text
                list.Append(i == 0 ? "" : ", ").Append(name);
                command.Parameters.AddId(name, blobIds[start + i]);
            }

            var ids = list.ToString();
            // Every table that references blobs (one index probe per id in each).
            command.CommandText = $"SELECT blob_id FROM objects WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM object_parts WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM object_versions WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM upload_parts WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM pending_blob_deletions WHERE blob_id IN ({ids});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                referenced.Add(reader.GetId(0));
        }

        var unreferenced = new List<Guid>();
        foreach (var id in blobIds)
        {
            if (!referenced.Contains(id))
                unreferenced.Add(id);
        }

        return unreferenced;
    }

    /// <summary>
    /// Records blobs a write released, in that write's transaction: they stay on disk until the collector's wait is
    /// over, and a crash after the commit cannot turn them into orphans that the sweep would delete early.
    /// </summary>
    private async ValueTask ReleaseBlobsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IEnumerable<BlobLocator>? blobs, CancellationToken ct)
    {
        if (blobs is null)
            return;
        SqliteCommand? command = null;
        try
        {
            foreach (var blob in blobs)
            {
                if (command is null)
                {
                    command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = ReleasedSql.Insert;
                    command.Parameters.AddId(ReleasedSql.BlobId, Guid.Empty);
                    command.Parameters.AddText(ReleasedSql.NodeId, string.Empty);
                    command.Parameters.AddInt64(ReleasedSql.Size, 0);
                    command.Parameters.AddInt64(ReleasedSql.ReleasedAt, SqliteTime.ToUnixMs(_clock.UtcNowMs));
                }

                command.Parameters[ReleasedSql.BlobId].Value = blob.BlobId.ToByteArray(bigEndian: true);
                command.Parameters[ReleasedSql.NodeId].Value = blob.NodeId;
                command.Parameters[ReleasedSql.Size].Value = blob.Size;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (command is not null)
                await command.DisposeAsync();
        }
    }

    public async ValueTask<IReadOnlyList<ReleasedBlob>> ListReleasedBlobsAsync(
        string nodeId, DateTime releasedBefore, int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReleasedSql.Due;
        command.Parameters.AddText(ReleasedSql.NodeId, nodeId);
        command.Parameters.AddInt64(ReleasedSql.ReleasedAt, SqliteTime.ToUnixMs(releasedBefore));
        command.Parameters.AddInt64(ReleasedSql.Limit, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var released = new List<ReleasedBlob>();
        while (await reader.ReadAsync(ct))
            released.Add(new ReleasedBlob(reader.GetId(0), reader.GetString(1), reader.GetInt64(2), SqliteTime.FromUnixMs(reader.GetInt64(3))));
        return released;
    }

    public async ValueTask ForgetReleasedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct)
    {
        if (blobIds.Count == 0)
            return;
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ReleasedSql.Forget;
            var id = command.Parameters.Add(ReleasedSql.BlobId, SqliteType.Blob);
            foreach (var blobId in blobIds)
            {
                id.Value = blobId.ToByteArray(bigEndian: true);
                await command.ExecuteNonQueryAsync(ct);
            }

            return blobIds.Count;
        });
    }

    public async ValueTask<RetainedBlobs> GetRetainedBlobsAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReleasedSql.Totals;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new RetainedBlobs(reader.GetInt64(0), reader.GetInt64(1)) : default;
    }

    private static class ReleasedSql
    {
        public const string BlobId = "$blobId";
        public const string NodeId = "$nodeId";
        public const string Size = "$size";
        public const string ReleasedAt = "$releasedAt";
        public const string Limit = "$limit";

        // A blob is released once (immutable, one owner); DO NOTHING keeps the first time if a write repeats it.
        public const string Insert = """
            INSERT INTO pending_blob_deletions (blob_id, node_id, size, released_at)
            VALUES ($blobId, $nodeId, $size, $releasedAt) ON CONFLICT (blob_id) DO NOTHING;
            """;

        public const string Due = """
            SELECT blob_id, node_id, size, released_at FROM pending_blob_deletions
            WHERE node_id = $nodeId AND released_at <= $releasedAt ORDER BY released_at LIMIT $limit;
            """;

        public const string Forget = "DELETE FROM pending_blob_deletions WHERE blob_id = $blobId;";
        public const string Totals = "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM pending_blob_deletions;";
    }
}
