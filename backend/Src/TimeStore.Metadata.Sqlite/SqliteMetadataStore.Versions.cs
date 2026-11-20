using Microsoft.Data.Sqlite;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Replication;
using TimeStore.Primitives;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// Versioning (step 6a, docs/research/s3-versioning.md). Invariant: an <c>objects</c> row is the key's latest version;
/// every other version and every delete marker is in <c>object_versions</c>. Internal version ids (UUIDv7, big-endian)
/// order versions by time; the null version has one too, so "latest" is always the newest id.
/// </summary>
public sealed partial class SqliteMetadataStore
{
    public async ValueTask<VersionLookup> FindObjectVersionAsync(
        Guid bucketId, string key, VersionRef version, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var current = await FindObjectAsync(connection, transaction: null, bucketId, key, ct);
        if (current is not null && Matches(current.VersionId, current.IsNullVersion, version))
            return new FoundVersion(current, IsLatest: true);

        var row = await FindVersionRowAsync(connection, transaction: null, bucketId, key, version, ct);
        if (row is null)
            return VersionMissing.Instance;
        var isLatest = current is null && await IsNewestHistoryRowAsync(connection, null, bucketId, key, row.VersionId, ct);
        return row.ToLookup(isLatest);
    }

    public async ValueTask<VersionLookup> FindLatestDeleteMarkerAsync(Guid bucketId, string key, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        if (await FindObjectAsync(connection, transaction: null, bucketId, key, ct) is not null)
            return VersionMissing.Instance;
        var newest = await FindNewestHistoryRowAsync(connection, transaction: null, bucketId, key, ct);
        return newest is { IsDeleteMarker: true } ? newest.ToLookup(isLatest: true) : VersionMissing.Instance;
    }

    public async ValueTask<DeletionBatch> DeleteObjectVersionsAsync(
        Guid bucketId, IReadOnlyList<ObjectToDelete> objects, CancellationToken ct)
    {
        var at = _clock.UtcNowMs;
        var queued = 0;
        var batch = await writer.WriteAsync(async (connection, transaction) =>
        {
            queued = 0; // a savepoint retry starts over
            var versioning = await ReadVersioningAsync(connection, transaction, bucketId, ct) ?? BucketVersioning.Unversioned;
            var results = new List<ObjectDeletion>(objects.Count);
            foreach (var target in objects)
            {
                var deletion = await DeleteOneAsync(connection, transaction, bucketId, versioning, target, at, ct);
                results.Add(deletion);
                await ReleaseBlobsAsync(connection, transaction, deletion.Released?.Blobs, ct);
                // Deleting the latest removes the key (delete); deleting one version may make an older one current, so
                // that is queued as a change of content: the worker sends whatever the key holds now either way.
                var operation = target.Version is null ? ReplicationOperation.Delete : ReplicationOperation.Put;
                queued += await QueueChangeAsync(connection, transaction, bucketId, target.Key, operation, at, ct);
            }

            return new DeletionBatch(results);
        });
        SignalReplication(queued);
        return batch;
    }

    /// <summary>The bucket's versioning state, or null when the bucket does not exist (read inside the write).</summary>
    private static async ValueTask<BucketVersioning?> ReadVersioningAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VersionSql.BucketVersioning;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return VersioningText.Parse(reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    /// <summary>
    /// Writes <paramref name="stored"/> as the key's latest version, as Amazon S3 does for the bucket's state, and
    /// returns the data no version references any more (to delete) with the version written.
    /// </summary>
    private static async ValueTask<(ObjectData? Released, ObjectVersion Version)> CommitVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredObject stored, StoredObject? current,
        BucketVersioning versioning, CancellationToken ct)
    {
        var newest = await NewestVersionIdAsync(connection, transaction, stored.BucketId, stored.Key, ct);
        stored = stored with { VersionId = VersionOrder.Next(stored.VersionId, newest) };
        switch (versioning)
        {
            case BucketVersioning.Unversioned:
                await WriteObjectAsync(connection, transaction, stored with { IsNullVersion = true }, current, ct);
                return (current?.Data, new ObjectVersion(stored.VersionId, true, versioning));

            case BucketVersioning.Enabled:
                if (current is not null)
                    await MoveCurrentToHistoryAsync(connection, transaction, current, stored.CreatedAt, ct);
                await SupersedeLatestMarkerAsync(connection, transaction, stored.BucketId, stored.Key, stored.CreatedAt, ct);
                await WriteObjectAsync(connection, transaction, stored with { IsNullVersion = false }, current: null, ct);
                return (null, new ObjectVersion(stored.VersionId, false, versioning));

            case BucketVersioning.Suspended:
                ObjectData? released;
                if (current is { IsNullVersion: true })
                {
                    released = current.Data; // the null version is replaced in place
                    await WriteObjectAsync(connection, transaction, stored with { IsNullVersion = true }, current, ct);
                }
                else
                {
                    if (current is not null)
                        await MoveCurrentToHistoryAsync(connection, transaction, current, stored.CreatedAt, ct);
                    released = await DeleteNullVersionInHistoryAsync(connection, transaction, stored.BucketId, stored.Key, ct);
                    await SupersedeLatestMarkerAsync(connection, transaction, stored.BucketId, stored.Key, stored.CreatedAt, ct);
                    await WriteObjectAsync(connection, transaction, stored with { IsNullVersion = true }, current: null, ct);
                }

                return (released, new ObjectVersion(stored.VersionId, true, versioning));

            default:
                throw new ArgumentOutOfRangeException(nameof(versioning));
        }
    }

    private static async ValueTask<ObjectDeletion> DeleteOneAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, BucketVersioning versioning,
        ObjectToDelete target, DateTime at, CancellationToken ct)
    {
        var key = target.Key;
        var current = await FindObjectAsync(connection, transaction, bucketId, key, ct);

        if (target.Version is not { } version)
        {
            switch (versioning)
            {
                case BucketVersioning.Unversioned:
                    if (current is null)
                        return new ObjectDeletion(key, null, null, IsDeleteMarker: false);
                    await DeleteCurrentAsync(connection, transaction, current, ct);
                    return new ObjectDeletion(key, current.Data, new ObjectVersion(current.VersionId, true, versioning), false);

                case BucketVersioning.Enabled:
                {
                    if (current is not null)
                        await MoveCurrentToHistoryAsync(connection, transaction, current, at, ct);
                    await SupersedeLatestMarkerAsync(connection, transaction, bucketId, key, at, ct);
                    var marker = VersionOrder.Next(Guid.CreateVersion7(at), await NewestVersionIdAsync(connection, transaction, bucketId, key, ct));
                    await InsertDeleteMarkerAsync(connection, transaction, bucketId, key, marker, isNull: false, at, ct);
                    return new ObjectDeletion(key, null, new ObjectVersion(marker, false, versioning), IsDeleteMarker: true);
                }

                case BucketVersioning.Suspended:
                {
                    ObjectData? released;
                    if (current is { IsNullVersion: true })
                    {
                        await DeleteCurrentAsync(connection, transaction, current, ct);
                        released = current.Data;
                    }
                    else
                    {
                        if (current is not null)
                            await MoveCurrentToHistoryAsync(connection, transaction, current, at, ct);
                        released = await DeleteNullVersionInHistoryAsync(connection, transaction, bucketId, key, ct);
                    }

                    await SupersedeLatestMarkerAsync(connection, transaction, bucketId, key, at, ct);
                    var marker = VersionOrder.Next(Guid.CreateVersion7(at), await NewestVersionIdAsync(connection, transaction, bucketId, key, ct));
                    await InsertDeleteMarkerAsync(connection, transaction, bucketId, key, marker, isNull: true, at, ct);
                    return new ObjectDeletion(key, released, new ObjectVersion(marker, true, versioning), IsDeleteMarker: true);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(versioning));
            }
        }

        // One version: exactly that one goes; the next newest becomes the latest.
        if (current is not null && Matches(current.VersionId, current.IsNullVersion, version))
        {
            await DeleteCurrentAsync(connection, transaction, current, ct);
            await PromoteNewestAsync(connection, transaction, bucketId, key, ct);
            return new ObjectDeletion(key, current.Data, new ObjectVersion(current.VersionId, current.IsNullVersion, versioning), false);
        }

        var row = await FindVersionRowAsync(connection, transaction, bucketId, key, version, ct);
        if (row is null)
            return new ObjectDeletion(key, null, new ObjectVersion(version.Id, version.IsNull, versioning), IsDeleteMarker: false);

        var wasLatest = current is null && await IsNewestHistoryRowAsync(connection, transaction, bucketId, key, row.VersionId, ct);
        var data = await DeleteHistoryRowAsync(connection, transaction, row, ct);
        if (wasLatest)
            await PromoteNewestAsync(connection, transaction, bucketId, key, ct);
        return new ObjectDeletion(key, data, new ObjectVersion(row.VersionId, row.IsNullVersion, versioning), row.IsDeleteMarker);
    }

    /// <summary>The newest internal id of the key in either table, or null for a new key.</summary>
    private static async ValueTask<Guid?> NewestVersionIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VersionSql.NewestId;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(0) ? reader.GetId(0) : null;
    }

    private static bool Matches(Guid id, bool isNull, VersionRef version) => version.IsNull ? isNull : id == version.Id;

    /// <summary>The current object leaves <c>objects</c> (its manifest stays: keyed by version).</summary>
    private static async ValueTask DeleteCurrentAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredObject current, CancellationToken ct)
    {
        if (current.Data is MultipartBlobs)
            await DeleteObjectPartsAsync(connection, transaction, current.VersionId, ct);
        await ExecuteAsync(connection, transaction, ObjectSql.Delete, current.BucketId, current.Key, ct);
    }

    /// <summary>The current object becomes noncurrent: moved to history with its data (manifest untouched).</summary>
    private static async ValueTask MoveCurrentToHistoryAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredObject current, DateTime supersededAt,
        CancellationToken ct)
    {
        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = VersionSql.MoveCurrentToHistory;
            move.Parameters.AddId(ObjectSql.BucketId, current.BucketId);
            move.Parameters.AddText(ObjectSql.Key, current.Key);
            move.Parameters.AddInt64(VersionSql.SupersededAt, SqliteTime.ToUnixMs(supersededAt));
            await move.ExecuteNonQueryAsync(ct);
        }

        await ExecuteAsync(connection, transaction, ObjectSql.Delete, current.BucketId, current.Key, ct);
    }

    /// <summary>A delete marker that was the latest version is noncurrent from now on.</summary>
    private static async ValueTask SupersedeLatestMarkerAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key, DateTime at, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VersionSql.SupersedeLatest;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        command.Parameters.AddInt64(VersionSql.SupersededAt, SqliteTime.ToUnixMs(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Removes the key's null version from history (an object or a marker); returns its data.</summary>
    private static async ValueTask<ObjectData?> DeleteNullVersionInHistoryAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key, CancellationToken ct)
    {
        var row = await FindVersionRowAsync(connection, transaction, bucketId, key, VersionRef.Null, ct);
        return row is null ? null : await DeleteHistoryRowAsync(connection, transaction, row, ct);
    }

    private static async ValueTask<ObjectData?> DeleteHistoryRowAsync(
        SqliteConnection connection, SqliteTransaction transaction, HistoryRow row, CancellationToken ct)
    {
        if (row.Object?.Data is MultipartBlobs)
            await DeleteObjectPartsAsync(connection, transaction, row.VersionId, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VersionSql.DeleteRow;
        command.Parameters.AddId(ObjectSql.BucketId, row.BucketId);
        command.Parameters.AddText(ObjectSql.Key, row.Key);
        command.Parameters.AddId(ObjectSql.VersionId, row.VersionId);
        await command.ExecuteNonQueryAsync(ct);
        return row.Object?.Data;
    }

    private static async ValueTask InsertDeleteMarkerAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key, Guid versionId, bool isNull,
        DateTime at, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VersionSql.InsertMarker;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        command.Parameters.AddId(ObjectSql.VersionId, versionId);
        command.Parameters.AddInt64(ObjectSql.NullVersion, isNull ? 1 : 0);
        command.Parameters.AddInt64(ObjectSql.CreatedAt, SqliteTime.ToUnixMs(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// After the latest version went: the newest remaining becomes current. An object moves back to <c>objects</c>; a
    /// delete marker stays in history as the latest (no longer superseded).
    /// </summary>
    private static async ValueTask PromoteNewestAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key, CancellationToken ct)
    {
        var newest = await FindNewestHistoryRowAsync(connection, transaction, bucketId, key, ct);
        if (newest is null)
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = newest.IsDeleteMarker ? VersionSql.MarkerBecomesLatest : VersionSql.PromoteToCurrent;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        command.Parameters.AddId(ObjectSql.VersionId, newest.VersionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask<bool> IsNewestHistoryRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid bucketId, string key, Guid versionId,
        CancellationToken ct) =>
        (await FindNewestHistoryRowAsync(connection, transaction, bucketId, key, ct))?.VersionId == versionId;

    private static ValueTask<HistoryRow?> FindNewestHistoryRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid bucketId, string key, CancellationToken ct) =>
        ReadHistoryRowAsync(connection, transaction, VersionSql.FindNewest, bucketId, key, null, ct);

    private static ValueTask<HistoryRow?> FindVersionRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid bucketId, string key, VersionRef version,
        CancellationToken ct) =>
        version.IsNull
            ? ReadHistoryRowAsync(connection, transaction, VersionSql.FindNull, bucketId, key, null, ct)
            : ReadHistoryRowAsync(connection, transaction, VersionSql.FindById, bucketId, key, version.Id, ct);

    private static async ValueTask<HistoryRow?> ReadHistoryRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, Guid bucketId, string key, Guid? versionId,
        CancellationToken ct)
    {
        HistoryRow row;
        int? partsCount;
        Guid? uploadId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddId(ObjectSql.BucketId, bucketId);
            command.Parameters.AddText(ObjectSql.Key, key);
            if (versionId is { } id)
                command.Parameters.AddId(ObjectSql.VersionId, id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var isNull = reader.GetInt64(16) != 0;
            var id2 = reader.GetId(2);
            if (reader.GetInt64(VersionSql.MarkerOrdinal) != 0)
                return new HistoryRow(bucketId, key, id2, isNull, SqliteTime.FromUnixMs(reader.GetInt64(12)), Object: null);

            (var stored, partsCount, uploadId) = ReadObjectRow(reader);
            row = new HistoryRow(bucketId, key, id2, isNull, stored.CreatedAt, stored);
        }

        if (partsCount is null)
            return row;
        var parts = await LoadObjectPartsAsync(connection, transaction, row.VersionId, ct);
        return row with { Object = row.Object! with { Data = new MultipartBlobs(uploadId ?? Guid.Empty, parts) } };
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, Guid bucketId, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A row of <c>object_versions</c>: an object version (with its data) or a delete marker.</summary>
    private sealed record HistoryRow(Guid BucketId, string Key, Guid VersionId, bool IsNullVersion, DateTime CreatedAt, StoredObject? Object)
    {
        public bool IsDeleteMarker => Object is null;

        public VersionLookup ToLookup(bool isLatest) => Object is { } stored
            ? new FoundVersion(stored, isLatest)
            : new FoundDeleteMarker(VersionId, IsNullVersion, CreatedAt, isLatest);
    }

    // --- ListObjectVersions ---------------------------------------------------------------------------------------

    public async ValueTask<VersionPage> ListObjectVersionsAsync(
        Guid bucketId, string prefix, string? delimiter, string? keyMarker, VersionRef? versionMarker, int maxEntries,
        CancellationToken ct)
    {
        var versions = new List<VersionSummary>();
        var prefixes = new List<string>();
        if (maxEntries <= 0)
            return new VersionPage(versions, prefixes, false, null, null);

        await using var connection = await connections.OpenAsync(ct);
        var useDelimiter = !string.IsNullOrEmpty(delimiter);
        string? lastPrefix = null;
        var lastWasPrefix = false;
        var afterKey = keyMarker ?? string.Empty;

        // Continue inside the marker key: its versions older than the marker version (never the latest).
        if (keyMarker is not null && versionMarker is { } marker)
        {
            var markerId = marker.IsNull ? await NullVersionIdAsync(connection, bucketId, keyMarker, ct) : marker.Id;
            if (markerId is { } olderThan)
            {
                foreach (var entry in await ReadVersionsAsync(connection, VersionSql.ListOlderInKey, bucketId, keyMarker, prefix, olderThan, maxEntries + 1, ct))
                {
                    if (versions.Count == maxEntries)
                        return Truncated();
                    versions.Add(entry with { IsLatest = false });
                    lastWasPrefix = false;
                }
            }
        }

        if (useDelimiter && keyMarker is not null && CommonPrefixOf(keyMarker, prefix, delimiter!) == keyMarker)
        {
            lastPrefix = keyMarker;
            afterKey = keyMarker + MaxChar;
        }

        string? previousKey = null;
        while (true)
        {
            var batch = await ReadVersionsAsync(connection, VersionSql.ListAfterKey, bucketId, afterKey, prefix, null, ListBatchSize, ct);
            var seeked = false;
            foreach (var row in batch)
            {
                if (!row.Key.StartsWith(prefix, StringComparison.Ordinal))
                    return Done(versions, prefixes);

                var common = useDelimiter ? CommonPrefixOf(row.Key, prefix, delimiter!) : null;
                if (common is not null)
                {
                    if (common == lastPrefix)
                        continue;
                    if (versions.Count + prefixes.Count == maxEntries)
                        return Truncated();
                    prefixes.Add(common);
                    lastPrefix = common;
                    lastWasPrefix = true;
                    afterKey = common + MaxChar;
                    seeked = true;
                    break;
                }

                if (versions.Count + prefixes.Count == maxEntries)
                    return Truncated();
                versions.Add(row with { IsLatest = row.Key != previousKey });
                lastWasPrefix = false;
                previousKey = row.Key;
                afterKey = row.Key; // the union query restarts after whole keys: finish this key inside the batch
            }

            if (seeked)
            {
                previousKey = null;
                continue;
            }

            if (batch.Count < ListBatchSize)
                return Done(versions, prefixes);

            // The batch ended inside a key or exactly at a key boundary: read that key's remaining versions.
            var lastKey = batch[^1].Key;
            var remaining = await ReadVersionsAsync(connection, VersionSql.ListOlderInKey, bucketId, lastKey, prefix, batch[^1].VersionId, int.MaxValue, ct);
            foreach (var row in remaining)
            {
                if (versions.Count + prefixes.Count == maxEntries)
                    return Truncated();
                versions.Add(row with { IsLatest = false });
                lastWasPrefix = false;
            }

            afterKey = lastKey;
            previousKey = lastKey;
        }

        static VersionPage Done(List<VersionSummary> versions, List<string> prefixes) =>
            new(versions, prefixes, false, null, null);

        // The next page starts after the last entry: a version (key + version id) or a common prefix (key only).
        VersionPage Truncated() => lastWasPrefix
            ? new VersionPage(versions, prefixes, true, lastPrefix, null)
            : new VersionPage(versions, prefixes, true, versions[^1].Key, versions[^1].VersionIdText);
    }

    private static async ValueTask<Guid?> NullVersionIdAsync(SqliteConnection connection, Guid bucketId, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = VersionSql.NullVersionId;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetId(0) : null;
    }

    /// <summary>Summaries in (key, newest version first) order; <c>IsLatest</c> is decided by the caller.</summary>
    private static async ValueTask<List<VersionSummary>> ReadVersionsAsync(
        SqliteConnection connection, string sql, Guid bucketId, string key, string prefix, Guid? olderThan, int limit,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddId(ObjectSql.BucketId, bucketId);
        command.Parameters.AddText(ObjectSql.Key, key);
        command.Parameters.AddText(ObjectSql.Prefix, prefix);
        command.Parameters.AddInt64(ObjectSql.Limit, limit);
        if (olderThan is { } id)
            command.Parameters.AddId(ObjectSql.VersionId, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<VersionSummary>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new VersionSummary(
                Key: reader.GetString(0),
                VersionId: reader.GetId(1),
                IsNullVersion: reader.GetInt64(2) != 0,
                IsDeleteMarker: reader.GetInt64(3) != 0,
                IsLatest: false,
                Size: reader.GetInt64(4),
                ETag: reader.GetString(5),
                CreatedAt: SqliteTime.FromUnixMs(reader.GetInt64(6)),
                SupersededAt: reader.IsDBNull(7) ? null : SqliteTime.FromUnixMs(reader.GetInt64(7))));
        }

        return rows;
    }

    private static class VersionSql
    {
        public const string SupersededAt = "$supersededAt";

        /// <summary>Ordinal of <c>is_delete_marker</c> after the shared object columns.</summary>
        public const int MarkerOrdinal = 17;

        private const string HistoryColumns = $"{ObjectSql.Columns}, is_delete_marker";

        public const string BucketVersioning = "SELECT versioning FROM buckets WHERE id = $bucketId;";

        public const string NewestId = """
            SELECT MAX(version_id) FROM (
                SELECT version_id FROM objects WHERE bucket_id = $bucketId AND key = $key
                UNION ALL
                SELECT MAX(version_id) FROM object_versions WHERE bucket_id = $bucketId AND key = $key);
            """;

        public const string MoveCurrentToHistory = $"""
            INSERT INTO object_versions ({ObjectSql.Columns}, is_delete_marker, superseded_at)
            SELECT {ObjectSql.Columns}, 0, $supersededAt FROM objects WHERE bucket_id = $bucketId AND key = $key;
            """;

        public const string SupersedeLatest = """
            UPDATE object_versions SET superseded_at = $supersededAt
            WHERE bucket_id = $bucketId AND key = $key AND superseded_at IS NULL;
            """;

        public const string InsertMarker = """
            INSERT INTO object_versions (bucket_id, key, version_id, null_version, is_delete_marker, size, etag,
                                         content_type, created_at)
            VALUES ($bucketId, $key, $versionId, $nullVersion, 1, 0, '', '', $createdAt);
            """;

        public const string DeleteRow = "DELETE FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND version_id = $versionId;";

        public const string FindById = $"SELECT {HistoryColumns} FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND version_id = $versionId;";

        public const string FindNull = $"SELECT {HistoryColumns} FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND null_version = 1;";

        public const string FindNewest = $"""
            SELECT {HistoryColumns} FROM object_versions WHERE bucket_id = $bucketId AND key = $key
            ORDER BY version_id DESC LIMIT 1;
            """;

        public const string PromoteToCurrent = $"""
            INSERT INTO objects ({ObjectSql.Columns})
            SELECT {ObjectSql.Columns} FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND version_id = $versionId;
            DELETE FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND version_id = $versionId;
            """;

        public const string MarkerBecomesLatest = """
            UPDATE object_versions SET superseded_at = NULL
            WHERE bucket_id = $bucketId AND key = $key AND version_id = $versionId;
            """;

        public const string NullVersionId = """
            SELECT version_id FROM objects WHERE bucket_id = $bucketId AND key = $key AND null_version = 1
            UNION ALL
            SELECT version_id FROM object_versions WHERE bucket_id = $bucketId AND key = $key AND null_version = 1;
            """;

        // Summary columns: key, version_id, null_version, is_delete_marker, size, etag, created_at, superseded_at.
        /// <summary>
        /// Keys after <c>$key</c> from the prefix on, all versions of each key newest first. Each side is limited before
        /// the merge, so a page sorts at most twice the limit.
        /// </summary>
        public const string ListAfterKey = """
            SELECT * FROM (
                SELECT key, version_id, null_version, 0 AS is_delete_marker, size, etag, created_at, NULL AS superseded_at FROM objects
                WHERE bucket_id = $bucketId AND key > $key AND key >= $prefix ORDER BY key LIMIT $limit)
            UNION ALL
            SELECT * FROM (
                SELECT key, version_id, null_version, is_delete_marker, size, etag, created_at, superseded_at FROM object_versions
                WHERE bucket_id = $bucketId AND key > $key AND key >= $prefix ORDER BY key, version_id DESC LIMIT $limit)
            ORDER BY key, version_id DESC LIMIT $limit;
            """;

        /// <summary>Versions of one key older than <c>$versionId</c>, newest first (the current row is always newest).</summary>
        public const string ListOlderInKey = """
            SELECT key, version_id, null_version, is_delete_marker, size, etag, created_at, superseded_at FROM object_versions
            WHERE bucket_id = $bucketId AND key = $key AND version_id < $versionId AND key >= $prefix
            ORDER BY version_id DESC LIMIT $limit;
            """;
    }
}
