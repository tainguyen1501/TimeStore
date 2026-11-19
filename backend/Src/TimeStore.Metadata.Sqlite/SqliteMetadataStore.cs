using Microsoft.Data.Sqlite;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// <see cref="IMetadataStore"/> on SQLite with raw ADO.NET (no EF, no Dapper: AOT). Reads use pooled connections;
/// every write goes through the one <see cref="SqliteWriter"/> (single writer, group commit).
/// </summary>
/// <param name="clock">
/// Times the store records itself (when a delete makes a version noncurrent). The services' clock, so lifecycle rules
/// and tests see one time; the system clock when not given.
/// </param>
public sealed partial class SqliteMetadataStore(SqliteConnectionFactory connections, TimeProvider? clock = null)
    : IMetadataStore, IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly SqliteWriter writer = new(connections);

    /// <summary>Transactions the writer has committed (tests: writes are grouped).</summary>
    public long CommittedBatches => writer.CommittedBatches;

    public ValueTask DisposeAsync() => writer.DisposeAsync();

    public async ValueTask<Bucket?> FindBucketAsync(string name, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = BucketSql.FindByName;
        command.Parameters.AddText(BucketSql.Name, name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBucket(reader) : null;
    }

    public async ValueTask<IReadOnlyList<Bucket>> ListBucketsAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = BucketSql.ListByName;

        await using var reader = await command.ExecuteReaderAsync(ct);
        var buckets = new List<Bucket>();
        while (await reader.ReadAsync(ct))
            buckets.Add(ReadBucket(reader));

        return buckets;
    }

    public async ValueTask<bool> TryCreateBucketAsync(Bucket bucket, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BucketSql.InsertIfNameFree;
            command.Parameters.AddId(BucketSql.Id, bucket.Id);
            command.Parameters.AddText(BucketSql.Name, bucket.Name);
            command.Parameters.AddInt64(BucketSql.CreatedAt, SqliteTime.ToUnixMs(bucket.CreatedAt));

            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<BucketDeletionResult> DeleteBucketIfEmptyAsync(string name, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = BucketSql.FindStateByName;
            find.Parameters.AddText(BucketSql.Name, name);
            Guid bucketId;
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    return new BucketDeletionResult(BucketDeletion.NotFound, []);
                if (reader.GetInt64(1) != 0)
                    return new BucketDeletionResult(BucketDeletion.NotEmpty, []);
                bucketId = reader.GetId(0);
            }

            // Uploads in progress go with the bucket (their parts cascade); hand their blobs back for deletion.
            var freed = new List<BlobLocator>();
            await using (var parts = connection.CreateCommand())
            {
                parts.Transaction = transaction;
                parts.CommandText = BucketSql.UploadPartBlobs;
                parts.Parameters.AddId(BucketSql.Id, bucketId);
                await using var reader = await parts.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    freed.Add(ReadBlob(reader, 0));
            }

            await using (var uploads = connection.CreateCommand())
            {
                uploads.Transaction = transaction;
                uploads.CommandText = BucketSql.DeleteUploads;
                uploads.Parameters.AddId(BucketSql.Id, bucketId);
                await uploads.ExecuteNonQueryAsync(ct);
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = BucketSql.DeleteByName;
            delete.Parameters.AddText(BucketSql.Name, name);
            await delete.ExecuteNonQueryAsync(ct);
            return new BucketDeletionResult(BucketDeletion.Deleted, freed);
        });
    }

    private static Bucket ReadBucket(SqliteDataReader reader) =>
        new(reader.GetId(0), reader.GetString(1), SqliteTime.FromUnixMs(reader.GetInt64(2)))
        {
            Versioning = VersioningText.Parse(reader.IsDBNull(3) ? null : reader.GetString(3)),
        };

    public async ValueTask<bool> SetBucketVersioningAsync(string name, BucketVersioning versioning, CancellationToken ct)
    {
        if (versioning == BucketVersioning.Unversioned)
            throw new ArgumentOutOfRangeException(nameof(versioning), "A bucket never returns to unversioned.");
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BucketSql.SetVersioning;
            command.Parameters.AddText(BucketSql.Name, name);
            command.Parameters.AddText(BucketSql.Versioning, VersioningText.Format(versioning)!);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    private static class BucketSql
    {
        public const string Id = "$id";
        public const string Name = "$name";
        public const string CreatedAt = "$createdAt";
        public const string Versioning = "$versioning";

        // Column order is read by ReadBucket.
        public const string FindByName = "SELECT id, name, created_at, versioning FROM buckets WHERE name = $name;";
        public const string ListByName = "SELECT id, name, created_at, versioning FROM buckets ORDER BY name;";
        public const string SetVersioning = "UPDATE buckets SET versioning = $versioning WHERE name = $name;";
        public const string InsertIfNameFree =
            "INSERT INTO buckets (id, name, created_at) VALUES ($id, $name, $createdAt) ON CONFLICT (name) DO NOTHING;";
        public const string DeleteByName = "DELETE FROM buckets WHERE name = $name;";

        /// <summary>
        /// Bucket id and whether it holds any object, noncurrent version or delete marker (1/0). Uploads in progress do
        /// not count.
        /// </summary>
        public const string FindStateByName = """
            SELECT id, EXISTS (SELECT 1 FROM objects o WHERE o.bucket_id = b.id)
                    OR EXISTS (SELECT 1 FROM object_versions v WHERE v.bucket_id = b.id)
            FROM buckets b WHERE b.name = $name;
            """;

        // Column order is read by ReadBlob.
        public const string UploadPartBlobs = """
            SELECT p.blob_id, p.node_id, p.size, p.etag FROM upload_parts p
            JOIN multipart_uploads u ON u.upload_id = p.upload_id WHERE u.bucket_id = $id;
            """;

        public const string DeleteUploads = "DELETE FROM multipart_uploads WHERE bucket_id = $id;";
    }
}
