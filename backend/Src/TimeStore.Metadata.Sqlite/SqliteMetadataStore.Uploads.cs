using Microsoft.Data.Sqlite;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Replication;
using TimeStore.Domain.Uploads;
using TimeStore.Primitives;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    /// <summary>Upload ids are compared as big-endian bytes: this is the greatest, used to seek past a whole key.</summary>
    private static readonly Guid MaxUploadId = new([.. Enumerable.Repeat((byte)0xFF, 16)], bigEndian: true);

    public async ValueTask<bool> TryCreateUploadAsync(MultipartUpload upload, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UploadSql.InsertIfBucketExists;
            var p = command.Parameters;
            p.AddId(UploadSql.UploadId, upload.UploadId);
            p.AddId(UploadSql.BucketId, upload.BucketId);
            p.AddText(UploadSql.Key, upload.Key);
            p.AddText(UploadSql.ContentType, upload.ContentType);
            p.AddNullableText(UploadSql.ChecksumAlgorithm, upload.ChecksumAlgorithm);
            p.AddNullableText(UploadSql.ChecksumType, upload.ChecksumType);
            p.AddNullableText(UploadSql.Headers, ObjectHeaderText.Encode(upload.Headers));
            p.AddNullableText(UploadSql.UserMetadata, ObjectHeaderText.Encode(upload.UserMetadata));
            p.AddInt64(UploadSql.CreatedAt, SqliteTime.ToUnixMs(upload.CreatedAt));
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<MultipartUpload?> FindUploadAsync(Guid bucketId, string key, Guid uploadId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await FindUploadAsync(connection, transaction: null, bucketId, key, uploadId, ct);
    }

    public async ValueTask<PartCommit> CommitPartAsync(UploadPart part, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = UploadSql.Exists;
                exists.Parameters.AddId(UploadSql.UploadId, part.UploadId);
                if (await exists.ExecuteScalarAsync(ct) is null)
                    return new PartCommit(PartCommitStatus.UploadMissing, Replaced: null);
            }

            BlobLocator? replaced = null;
            await using (var previous = connection.CreateCommand())
            {
                previous.Transaction = transaction;
                previous.CommandText = UploadSql.FindPartBlob;
                previous.Parameters.AddId(UploadSql.UploadId, part.UploadId);
                previous.Parameters.AddInt64(UploadSql.PartNumber, part.PartNumber);
                await using var reader = await previous.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    replaced = ReadBlob(reader, 0);
            }

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = UploadSql.UpsertPart;
                var p = upsert.Parameters;
                p.AddId(UploadSql.UploadId, part.UploadId);
                p.AddInt64(UploadSql.PartNumber, part.PartNumber);
                p.AddId(UploadSql.BlobId, part.Blob.BlobId);
                p.AddText(UploadSql.NodeId, part.Blob.NodeId);
                p.AddInt64(UploadSql.Size, part.Blob.Size);
                p.AddText(UploadSql.ETag, part.ETag);
                p.AddNullableBlob(UploadSql.Checksum, part.Checksum);
                p.AddInt64(UploadSql.CreatedAt, SqliteTime.ToUnixMs(part.CreatedAt));
                await upsert.ExecuteNonQueryAsync(ct);
            }

            if (replaced is not null)
                await ReleaseBlobsAsync(connection, transaction, [replaced], ct);
            return new PartCommit(PartCommitStatus.Committed, replaced);
        });
    }

    public async ValueTask<PartPage> ListPartsAsync(Guid uploadId, int afterPartNumber, int maxParts, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var parts = await ListPartsAsync(connection, transaction: null, uploadId, afterPartNumber, maxParts + 1, ct);
        var truncated = parts.Count > maxParts;
        if (truncated)
            parts.RemoveAt(parts.Count - 1);
        return new PartPage(parts, truncated);
    }

    public async ValueTask<UploadPage> ListUploadsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, Guid? uploadIdMarker, int maxEntries,
        CancellationToken ct)
    {
        var uploads = new List<UploadSummary>();
        var prefixes = new List<string>();
        if (maxEntries <= 0)
            return new UploadPage(uploads, prefixes, IsTruncated: false, NextKeyMarker: null, NextUploadIdMarker: null);

        var useDelimiter = !string.IsNullOrEmpty(delimiter);
        // Without an upload id marker the listing starts after every upload of the key marker.
        var cursorKey = keyMarker ?? string.Empty;
        var cursorId = keyMarker is null ? Guid.Empty : uploadIdMarker ?? MaxUploadId;
        string? lastPrefix = null;
        string? nextKey = null;
        Guid? nextId = null;

        if (useDelimiter && keyMarker is not null && CommonPrefixOf(keyMarker, prefix, delimiter!) == keyMarker)
        {
            lastPrefix = keyMarker;
            (cursorKey, cursorId) = (keyMarker + MaxChar, MaxUploadId);
        }

        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = UploadSql.ListFrom;
        command.Parameters.AddId(UploadSql.BucketId, bucketId);
        var keyParameter = command.Parameters.Add(UploadSql.CursorKey, SqliteType.Text);
        var idParameter = command.Parameters.Add(UploadSql.CursorId, SqliteType.Blob);
        command.Parameters.AddText(UploadSql.Prefix, prefix);
        command.Parameters.AddInt64(UploadSql.Limit, ListBatchSize);

        while (true)
        {
            keyParameter.Value = cursorKey;
            idParameter.Value = cursorId.ToByteArray(bigEndian: true);
            var read = 0;
            var seeked = false;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    read++;
                    var upload = new UploadSummary(reader.GetString(0), reader.GetId(1), SqliteTime.FromUnixMs(reader.GetInt64(2)));
                    if (!upload.Key.StartsWith(prefix, StringComparison.Ordinal))
                        return new UploadPage(uploads, prefixes, IsTruncated: false, nextKey, nextId);

                    var common = useDelimiter ? CommonPrefixOf(upload.Key, prefix, delimiter!) : null;
                    if (common is not null && common == lastPrefix)
                    {
                        (cursorKey, cursorId) = (upload.Key, upload.UploadId);
                        continue;
                    }

                    if (uploads.Count + prefixes.Count == maxEntries)
                        return new UploadPage(uploads, prefixes, IsTruncated: true, nextKey, nextId);

                    if (common is not null)
                    {
                        prefixes.Add(common);
                        lastPrefix = nextKey = common;
                        nextId = null;
                        (cursorKey, cursorId) = (common + MaxChar, MaxUploadId);
                        seeked = true;
                        break;
                    }

                    uploads.Add(upload);
                    (nextKey, nextId) = (upload.Key, upload.UploadId);
                    (cursorKey, cursorId) = (upload.Key, upload.UploadId);
                }
            }

            if (!seeked && read < ListBatchSize)
                return new UploadPage(uploads, prefixes, IsTruncated: false, nextKey, nextId);
        }
    }

    public async ValueTask<IReadOnlyList<UploadRef>> ListUploadsCreatedBeforeAsync(
        DateTime createdBefore, int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = UploadSql.CreatedBefore;
        command.Parameters.AddInt64(UploadSql.CreatedAt, SqliteTime.ToUnixMs(createdBefore));
        command.Parameters.AddInt64(UploadSql.Limit, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var uploads = new List<UploadRef>();
        while (await reader.ReadAsync(ct))
            uploads.Add(new UploadRef(reader.GetId(0), reader.GetString(1), reader.GetId(2)));
        return uploads;
    }

    public async ValueTask<IReadOnlyList<BlobLocator>?> AbortUploadAsync(
        Guid bucketId, string key, Guid uploadId, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            if (await FindUploadAsync(connection, transaction, bucketId, key, uploadId, ct) is null)
                return null;

            var parts = await ListPartsAsync(connection, transaction, uploadId, afterPartNumber: 0, int.MaxValue, ct);
            await DeleteUploadAsync(connection, transaction, uploadId, ct);
            var blobs = parts.ConvertAll(p => p.Blob);
            await ReleaseBlobsAsync(connection, transaction, blobs, ct);
            return blobs;
        });
    }

    public async ValueTask<UploadCompletion> CompleteUploadAsync(
        StoredObject stored, CommitCondition condition, CancellationToken ct)
    {
        var queued = 0;
        var completion = await writer.WriteAsync(async (connection, transaction) =>
        {
            if (stored.Data is not MultipartBlobs manifest)
                throw new ArgumentException("CompleteUploadAsync commits multipart objects only.", nameof(stored));


            if (await ReadVersioningAsync(connection, transaction, stored.BucketId, ct) is not { } versioning)
                return new UploadCompletion(CompletionStatus.BucketMissing, null, []);
            if (await FindUploadAsync(connection, transaction, stored.BucketId, stored.Key, manifest.UploadId, ct) is null)
                return new UploadCompletion(CompletionStatus.UploadMissing, null, []);

            // Every listed part must still be the part the caller validated (a concurrent re-upload changes the blob).
            var uploaded = await ListPartsAsync(connection, transaction, manifest.UploadId, afterPartNumber: 0, int.MaxValue, ct);
            var byNumber = uploaded.ToDictionary(p => p.PartNumber);
            foreach (var part in manifest.Parts)
            {
                if (!byNumber.TryGetValue(part.PartNumber, out var current) || current.Blob.BlobId != part.Blob.BlobId)
                    return new UploadCompletion(CompletionStatus.PartsChanged, null, []);
            }

            var existing = await FindObjectAsync(connection, transaction, stored.BucketId, stored.Key, ct);
            if (existing is not null && condition == CommitCondition.IfAbsent)
                return new UploadCompletion(CompletionStatus.ConditionFailed, null, []);

            var (released, version) = await CommitVersionAsync(connection, transaction, stored, existing, versioning, ct);
            await DeleteUploadAsync(connection, transaction, manifest.UploadId, ct);
            // The commit's time, not the object's (a multipart object carries the upload's start): the lag shown is real.
            queued = await QueueChangeAsync(connection, transaction, stored.BucketId, stored.Key, ReplicationOperation.Put, _clock.UtcNowMs, ct);

            var listed = manifest.Parts.Select(p => p.PartNumber).ToHashSet();
            var discarded = uploaded.Where(p => !listed.Contains(p.PartNumber)).Select(p => p.Blob).ToArray();
            await ReleaseBlobsAsync(connection, transaction, released?.Blobs, ct);
            await ReleaseBlobsAsync(connection, transaction, discarded, ct);
            return new UploadCompletion(CompletionStatus.Completed, released, discarded, version);
        });
        SignalReplication(queued);
        return completion;
    }

    private static async ValueTask<MultipartUpload?> FindUploadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid bucketId, string key, Guid uploadId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UploadSql.Find;
        command.Parameters.AddId(UploadSql.UploadId, uploadId);
        command.Parameters.AddId(UploadSql.BucketId, bucketId);
        command.Parameters.AddText(UploadSql.Key, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new MultipartUpload(
            UploadId: reader.GetId(0),
            BucketId: reader.GetId(1),
            Key: reader.GetString(2),
            ContentType: reader.GetString(3),
            ChecksumAlgorithm: reader.IsDBNull(4) ? null : reader.GetString(4),
            ChecksumType: reader.IsDBNull(5) ? null : reader.GetString(5),
            Headers: ObjectHeaderText.Decode(reader.IsDBNull(6) ? null : reader.GetString(6)),
            UserMetadata: ObjectHeaderText.Decode(reader.IsDBNull(7) ? null : reader.GetString(7)),
            CreatedAt: SqliteTime.FromUnixMs(reader.GetInt64(8)));
    }

    private static async ValueTask<List<UploadPart>> ListPartsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid uploadId, int afterPartNumber, int limit,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UploadSql.ListParts;
        command.Parameters.AddId(UploadSql.UploadId, uploadId);
        command.Parameters.AddInt64(UploadSql.PartNumber, afterPartNumber);
        command.Parameters.AddInt64(UploadSql.Limit, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var parts = new List<UploadPart>();
        while (await reader.ReadAsync(ct))
        {
            parts.Add(new UploadPart(
                UploadId: uploadId,
                PartNumber: reader.GetInt32(0),
                Blob: ReadBlob(reader, 1),
                Checksum: reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
                CreatedAt: SqliteTime.FromUnixMs(reader.GetInt64(6))));
        }

        return parts;
    }

    /// <summary>Removes the upload; its part rows go with it (ON DELETE CASCADE).</summary>
    private static async ValueTask DeleteUploadAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid uploadId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UploadSql.Delete;
        command.Parameters.AddId(UploadSql.UploadId, uploadId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static class UploadSql
    {
        public const string UploadId = "$uploadId";
        public const string BucketId = "$bucketId";
        public const string Key = "$key";
        public const string ContentType = "$contentType";
        public const string ChecksumAlgorithm = "$checksumAlgorithm";
        public const string ChecksumType = "$checksumType";
        public const string Headers = "$headers";
        public const string UserMetadata = "$userMetadata";
        public const string CreatedAt = "$createdAt";
        public const string PartNumber = "$partNumber";
        public const string BlobId = "$blobId";
        public const string NodeId = "$nodeId";
        public const string Size = "$size";
        public const string ETag = "$etag";
        public const string Checksum = "$checksum";
        public const string CursorKey = "$cursorKey";
        public const string CursorId = "$cursorId";
        public const string Prefix = "$prefix";
        public const string Limit = "$limit";

        public const string InsertIfBucketExists = """
            INSERT INTO multipart_uploads (upload_id, bucket_id, key, content_type, checksum_algorithm, checksum_type,
                                           headers, user_metadata, created_at)
            SELECT $uploadId, $bucketId, $key, $contentType, $checksumAlgorithm, $checksumType, $headers, $userMetadata,
                   $createdAt
            WHERE EXISTS (SELECT 1 FROM buckets WHERE id = $bucketId);
            """;

        // Column order is read by FindUploadAsync.
        public const string Find = """
            SELECT upload_id, bucket_id, key, content_type, checksum_algorithm, checksum_type, headers, user_metadata,
                   created_at
            FROM multipart_uploads WHERE upload_id = $uploadId AND bucket_id = $bucketId AND key = $key;
            """;

        public const string Exists = "SELECT 1 FROM multipart_uploads WHERE upload_id = $uploadId;";

        public const string FindPartBlob = """
            SELECT blob_id, node_id, size, etag FROM upload_parts
            WHERE upload_id = $uploadId AND part_number = $partNumber;
            """;

        public const string UpsertPart = """
            INSERT INTO upload_parts (upload_id, part_number, blob_id, node_id, size, etag, checksum, created_at)
            VALUES ($uploadId, $partNumber, $blobId, $nodeId, $size, $etag, $checksum, $createdAt)
            ON CONFLICT (upload_id, part_number) DO UPDATE SET
                blob_id = excluded.blob_id, node_id = excluded.node_id, size = excluded.size, etag = excluded.etag,
                checksum = excluded.checksum, created_at = excluded.created_at;
            """;

        // Column order is read by ListPartsAsync (blob columns 1-4 by ReadBlob).
        public const string ListParts = """
            SELECT part_number, blob_id, node_id, size, etag, checksum, created_at FROM upload_parts
            WHERE upload_id = $uploadId AND part_number > $partNumber
            ORDER BY part_number LIMIT $limit;
            """;

        /// <summary>Range scan on ix_uploads_key: (key, upload id) after the cursor, from the prefix on.</summary>
        public const string ListFrom = """
            SELECT key, upload_id, created_at FROM multipart_uploads
            WHERE bucket_id = $bucketId AND key >= $prefix
              AND (key > $cursorKey OR (key = $cursorKey AND upload_id > $cursorId))
            ORDER BY key, upload_id LIMIT $limit;
            """;

        public const string Delete = "DELETE FROM multipart_uploads WHERE upload_id = $uploadId;";

        /// <summary>Range scan on ix_uploads_created.</summary>
        public const string CreatedBefore = """
            SELECT bucket_id, key, upload_id FROM multipart_uploads
            WHERE created_at < $createdAt ORDER BY created_at LIMIT $limit;
            """;
    }
}
