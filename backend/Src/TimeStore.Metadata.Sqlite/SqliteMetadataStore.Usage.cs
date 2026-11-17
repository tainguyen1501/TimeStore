using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    public async ValueTask<IReadOnlyList<BucketUsage>> ListBucketUsageAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = UsageSql.Buckets;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var usage = new List<BucketUsage>();
        while (await reader.ReadAsync(ct))
            usage.Add(new BucketUsage(
                reader.GetId(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
        return usage;
    }

    public async ValueTask<UploadUsage> GetUploadUsageAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = UsageSql.Uploads;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new UploadUsage(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static class UsageSql
    {
        // Column order is read above.
        public const string Buckets = """
            SELECT b.id, b.name, u.object_count, u.total_bytes, u.version_count, u.version_bytes
            FROM buckets b JOIN bucket_usage u ON u.bucket_id = b.id
            ORDER BY b.name;
            """;

        public const string Uploads = """
            SELECT (SELECT COUNT(*) FROM multipart_uploads),
                   (SELECT COUNT(*) FROM upload_parts),
                   (SELECT COALESCE(SUM(size), 0) FROM upload_parts);
            """;
    }
}
