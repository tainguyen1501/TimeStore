using Microsoft.Data.Sqlite;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    /// <summary>Rows read per query while listing plain keys (a common prefix ends the batch with a seek).</summary>
    private const int ListBatchSize = 256;

    /// <summary>Highest code point: <c>prefix + MaxChar</c> sorts after every key that starts with the prefix.</summary>
    private const string MaxChar = "\U0010FFFF";

    public async ValueTask<StoredObject?> FindObjectAsync(Guid bucketId, string key, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await FindObjectAsync(connection, transaction: null, bucketId, key, ct);
    }

    public async ValueTask<ObjectCommit> CommitObjectAsync(
        StoredObject stored, CommitCondition condition, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            if (stored.Data is not SingleBlob)
                throw new ArgumentException("Multipart objects are committed by CompleteUploadAsync.", nameof(stored));


            if (await ReadVersioningAsync(connection, transaction, stored.BucketId, ct) is not { } versioning)
                return new ObjectCommit(CommitStatus.BucketMissing, Replaced: null);

            var current = await FindObjectAsync(connection, transaction, stored.BucketId, stored.Key, ct);
            if (current is not null && condition == CommitCondition.IfAbsent)
                return new ObjectCommit(CommitStatus.ConditionFailed, Replaced: null);

            var (released, version) = await CommitVersionAsync(connection, transaction, stored, current, versioning, ct);
            return new ObjectCommit(CommitStatus.Committed, released, version);
        });
    }

    /// <summary>Deletes the latest (a delete marker in a versioned bucket); returns data no version references.</summary>
    public async ValueTask<ObjectData?> DeleteObjectAsync(Guid bucketId, string key, CancellationToken ct)
    {
        var batch = await DeleteObjectVersionsAsync(bucketId, [new ObjectToDelete(key, Version: null)], ct);
        return batch.Results[0].Released;
    }

    public async ValueTask<IReadOnlyList<BlobLocator>> DeleteObjectsAsync(
        Guid bucketId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var batch = await DeleteObjectVersionsAsync(bucketId, keys.Select(static k => new ObjectToDelete(k, null)).ToArray(), ct);
        return batch.ReleasedBlobs.ToArray();
    }

    /// <summary>
    /// Skip-scan listing: plain keys are read in batches; a common prefix is emitted once and the cursor then seeks
    /// past every key under it (<c>prefix + MaxChar</c>), so its size never matters. Reads summaries only: listing
    /// never loads part manifests.
    /// </summary>
    public async ValueTask<ObjectPage> ListObjectsAsync(
        Guid bucketId, string prefix, string? delimiter, string? startAfter, int maxEntries, CancellationToken ct)
    {
        var objects = new List<ObjectSummary>();
        var prefixes = new List<string>();
        if (maxEntries <= 0)
            return new ObjectPage(objects, prefixes, IsTruncated: false, LastEntry: null);

        var useDelimiter = !string.IsNullOrEmpty(delimiter);
        var cursor = startAfter ?? string.Empty;
        string? lastPrefix = null;
        string? lastEntry = null;

        // A page that ended on a common prefix resumes after all of its keys.
        if (useDelimiter && startAfter is not null && CommonPrefixOf(startAfter, prefix, delimiter!) == startAfter)
        {
            lastPrefix = startAfter;
            cursor = startAfter + MaxChar;
        }

        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ObjectSql.ListFrom;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        var cursorParameter = command.Parameters.Add(ObjectSql.Cursor, SqliteType.Text);
        command.Parameters.AddText(ObjectSql.Prefix, prefix);
        command.Parameters.AddInt64(ObjectSql.Limit, ListBatchSize);

        while (true)
        {
            cursorParameter.Value = cursor;
            var read = 0;
            var seeked = false;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    read++;
                    var summary = new ObjectSummary(
                        reader.GetString(0), reader.GetInt64(1), reader.GetString(2), SqliteTime.FromUnixMs(reader.GetInt64(3)));
                    if (!summary.Key.StartsWith(prefix, StringComparison.Ordinal))
                        return new ObjectPage(objects, prefixes, IsTruncated: false, lastEntry);

                    var common = useDelimiter ? CommonPrefixOf(summary.Key, prefix, delimiter!) : null;
                    if (common is not null && common == lastPrefix)
                    {
                        cursor = summary.Key; // a key sorting after prefix + MaxChar inside the same prefix
                        continue;
                    }

                    if (objects.Count + prefixes.Count == maxEntries)
                        return new ObjectPage(objects, prefixes, IsTruncated: true, lastEntry);

                    if (common is not null)
                    {
                        prefixes.Add(common);
                        lastPrefix = lastEntry = common;
                        cursor = common + MaxChar;
                        seeked = true;
                        break; // re-query from past the whole prefix
                    }

                    objects.Add(summary);
                    lastEntry = cursor = summary.Key;
                }
            }

            if (!seeked && read < ListBatchSize)
                return new ObjectPage(objects, prefixes, IsTruncated: false, lastEntry);
        }
    }

    /// <summary>The key up to and including the first delimiter after the prefix, or null.</summary>
    private static string? CommonPrefixOf(string key, string prefix, string delimiter)
    {
        if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var index = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
        return index < 0 ? null : key[..(index + delimiter.Length)];
    }

    private static async ValueTask<bool> BucketExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ObjectSql.BucketExists;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>The object with its data (a multipart object's manifest is a second, primary-key range read).</summary>
    private static async ValueTask<StoredObject?> FindObjectAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid bucketId, string key, CancellationToken ct)
    {
        StoredObject stored;
        int? partsCount;
        Guid? uploadId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = ObjectSql.Find;
            command.Parameters.AddId(ObjectSql.BucketId, bucketId);
            command.Parameters.AddText(ObjectSql.Key, key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            (stored, partsCount, uploadId) = ReadObjectRow(reader);
        }

        if (partsCount is null)
            return stored;

        var parts = await LoadObjectPartsAsync(connection, transaction, stored.VersionId, ct);
        if (parts.Count != partsCount)
            throw new InvalidDataException($"Object version {stored.VersionId} lists {partsCount} parts but has {parts.Count}.");
        return stored with { Data = new MultipartBlobs(uploadId ?? Guid.Empty, parts) };
    }

    /// <summary>
    /// Reads <see cref="ObjectSql.Columns"/> (objects and object_versions share them); a multipart object's data is a
    /// placeholder until its parts are loaded.
    /// </summary>
    private static (StoredObject Stored, int? PartsCount, Guid? UploadId) ReadObjectRow(SqliteDataReader reader)
    {
        var partsCount = reader.IsDBNull(13) ? null : (int?)reader.GetInt32(13);
        var uploadId = reader.IsDBNull(14) ? null : (Guid?)reader.GetId(14);
        var stored = new StoredObject(
                BucketId: reader.GetId(0),
                Key: reader.GetString(1),
                VersionId: reader.GetId(2),
                Data: partsCount is null ? new SingleBlob(ReadBlob(reader, 3)) : new MultipartBlobs(Guid.Empty, []),
                ETag: reader.GetString(6),
                ContentType: reader.GetString(7),
                ChecksumAlgorithm: reader.IsDBNull(8) ? null : reader.GetString(8),
                Checksum: reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9),
                ChecksumType: reader.IsDBNull(15) ? null : reader.GetString(15),
                Headers: ObjectHeaderText.Decode(reader.IsDBNull(10) ? null : reader.GetString(10)),
                UserMetadata: ObjectHeaderText.Decode(reader.IsDBNull(11) ? null : reader.GetString(11)),
                CreatedAt: SqliteTime.FromUnixMs(reader.GetInt64(12)))
        {
            IsNullVersion = reader.GetInt64(16) != 0,
        };
        return (stored, partsCount, uploadId);
    }

    private static async ValueTask<List<ObjectPart>> LoadObjectPartsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid versionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ObjectSql.FindParts;
        command.Parameters.AddId(ObjectSql.VersionId, versionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var parts = new List<ObjectPart>();
        while (await reader.ReadAsync(ct))
        {
            parts.Add(new ObjectPart(
                PartNumber: reader.GetInt32(0),
                Offset: reader.GetInt64(1),
                Blob: ReadBlob(reader, 2),
                Checksum: reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6)));
        }

        return parts;
    }

    /// <summary>Writes <paramref name="stored"/> over <paramref name="current"/> (whose manifest, if any, is removed).</summary>
    private static async ValueTask WriteObjectAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredObject stored, StoredObject? current,
        CancellationToken ct)
    {
        if (current?.Data is MultipartBlobs)
            await DeleteObjectPartsAsync(connection, transaction, current.VersionId, ct);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = ObjectSql.Upsert;
            var p = upsert.Parameters;
            p.AddId(ObjectSql.BucketId, stored.BucketId);
            p.AddText(ObjectSql.Key, stored.Key);
            p.AddId(ObjectSql.VersionId, stored.VersionId);
            p.AddInt64(ObjectSql.Size, stored.Size);
            p.AddText(ObjectSql.ETag, stored.ETag);
            p.AddText(ObjectSql.ContentType, stored.ContentType);
            p.AddNullableText(ObjectSql.ChecksumAlgorithm, stored.ChecksumAlgorithm);
            p.AddNullableBlob(ObjectSql.Checksum, stored.Checksum);
            p.AddNullableText(ObjectSql.ChecksumType, stored.ChecksumType);
            p.AddNullableText(ObjectSql.Headers, ObjectHeaderText.Encode(stored.Headers));
            p.AddNullableText(ObjectSql.UserMetadata, ObjectHeaderText.Encode(stored.UserMetadata));
            p.AddInt64(ObjectSql.CreatedAt, SqliteTime.ToUnixMs(stored.CreatedAt));
            p.AddInt64(ObjectSql.NullVersion, stored.IsNullVersion ? 1 : 0);
            switch (stored.Data)
            {
                case SingleBlob single:
                    p.AddNullableBlob(ObjectSql.BlobId, single.Blob.BlobId.ToByteArray(bigEndian: true));
                    p.AddNullableText(ObjectSql.NodeId, single.Blob.NodeId);
                    p.AddNullableInt64(ObjectSql.PartsCount, null);
                    p.AddNullableBlob(ObjectSql.UploadId, null);
                    break;
                case MultipartBlobs multipart:
                    p.AddNullableBlob(ObjectSql.BlobId, null);
                    p.AddNullableText(ObjectSql.NodeId, null);
                    p.AddNullableInt64(ObjectSql.PartsCount, multipart.Parts.Count);
                    p.AddNullableBlob(ObjectSql.UploadId, multipart.UploadId.ToByteArray(bigEndian: true));
                    break;
            }

            await upsert.ExecuteNonQueryAsync(ct);
        }

        if (stored.Data is not MultipartBlobs { Parts: var parts })
            return;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = ObjectSql.InsertPart;
        insert.Parameters.AddId(ObjectSql.VersionId, stored.VersionId);
        var number = insert.Parameters.Add(ObjectSql.PartNumber, SqliteType.Integer);
        var offset = insert.Parameters.Add(ObjectSql.Offset, SqliteType.Integer);
        var blobId = insert.Parameters.Add(ObjectSql.BlobId, SqliteType.Blob);
        var nodeId = insert.Parameters.Add(ObjectSql.NodeId, SqliteType.Text);
        var size = insert.Parameters.Add(ObjectSql.Size, SqliteType.Integer);
        var etag = insert.Parameters.Add(ObjectSql.ETag, SqliteType.Text);
        var checksum = insert.Parameters.Add(ObjectSql.Checksum, SqliteType.Blob);
        foreach (var part in parts)
        {
            number.Value = part.PartNumber;
            offset.Value = part.Offset;
            blobId.Value = part.Blob.BlobId.ToByteArray(bigEndian: true);
            nodeId.Value = part.Blob.NodeId;
            size.Value = part.Blob.Size;
            etag.Value = part.ETag;
            checksum.Value = (object?)part.Checksum ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async ValueTask DeleteObjectPartsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid versionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ObjectSql.DeleteParts;
        command.Parameters.AddId(ObjectSql.VersionId, versionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reads blob_id, node_id, size, etag starting at <paramref name="first"/>.</summary>
    private static BlobLocator ReadBlob(SqliteDataReader reader, int first) =>
        new(reader.GetId(first), reader.GetString(first + 1), reader.GetInt64(first + 2), reader.GetString(first + 3));

    private static class ObjectSql
    {
        public const string BucketId = "$bucketId";
        public const string Key = "$key";
        public const string VersionId = "$versionId";
        public const string BlobId = "$blobId";
        public const string NodeId = "$nodeId";
        public const string Size = "$size";
        public const string ETag = "$etag";
        public const string ContentType = "$contentType";
        public const string ChecksumAlgorithm = "$checksumAlgorithm";
        public const string Checksum = "$checksum";
        public const string ChecksumType = "$checksumType";
        public const string Headers = "$headers";
        public const string UserMetadata = "$userMetadata";
        public const string CreatedAt = "$createdAt";
        public const string PartsCount = "$partsCount";
        public const string UploadId = "$uploadId";
        public const string PartNumber = "$partNumber";
        public const string Offset = "$offset";
        public const string Cursor = "$cursor";
        public const string Prefix = "$prefix";
        public const string Limit = "$limit";
        public const string NullVersion = "$nullVersion";

        // Column order is read by ReadObjectRow (blob columns 3-6 by ReadBlob). object_versions has the same columns.
        public const string Columns =
            "bucket_id, key, version_id, blob_id, node_id, size, etag, content_type, checksum_algorithm, checksum, "
            + "headers, user_metadata, created_at, parts_count, upload_id, checksum_type, null_version";

        public const string Find = $"SELECT {Columns} FROM objects WHERE bucket_id = $bucketId AND key = $key;";

        public const string BucketExists = "SELECT 1 FROM buckets WHERE id = $bucketId;";

        public const string Upsert = $"""
            INSERT INTO objects ({Columns})
            VALUES ($bucketId, $key, $versionId, $blobId, $nodeId, $size, $etag, $contentType, $checksumAlgorithm,
                    $checksum, $headers, $userMetadata, $createdAt, $partsCount, $uploadId, $checksumType, $nullVersion)
            ON CONFLICT (bucket_id, key) DO UPDATE SET
                version_id = excluded.version_id, blob_id = excluded.blob_id, node_id = excluded.node_id,
                size = excluded.size, etag = excluded.etag, content_type = excluded.content_type,
                checksum_algorithm = excluded.checksum_algorithm, checksum = excluded.checksum,
                headers = excluded.headers, user_metadata = excluded.user_metadata, created_at = excluded.created_at,
                parts_count = excluded.parts_count, upload_id = excluded.upload_id, checksum_type = excluded.checksum_type,
                null_version = excluded.null_version;
            """;

        public const string Delete = "DELETE FROM objects WHERE bucket_id = $bucketId AND key = $key;";

        // Column order is read by LoadObjectPartsAsync (blob columns 2-5 by ReadBlob).
        public const string FindParts = """
            SELECT part_number, offset, blob_id, node_id, size, etag, checksum
            FROM object_parts WHERE version_id = $versionId ORDER BY part_number;
            """;

        public const string InsertPart = """
            INSERT INTO object_parts (version_id, part_number, offset, blob_id, node_id, size, etag, checksum)
            VALUES ($versionId, $partNumber, $offset, $blobId, $nodeId, $size, $etag, $checksum);
            """;

        public const string DeleteParts = "DELETE FROM object_parts WHERE version_id = $versionId;";

        /// <summary>Range scan on the primary key: keys after the cursor, from the prefix on.</summary>
        public const string ListFrom = """
            SELECT key, size, etag, created_at FROM objects
            WHERE bucket_id = $bucketId AND key > $cursor AND key >= $prefix
            ORDER BY key LIMIT $limit;
            """;
    }
}
