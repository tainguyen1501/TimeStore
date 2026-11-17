using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    public async ValueTask<string?> GetBucketPolicyAsync(Guid bucketId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = PolicySql.Find;
        command.Parameters.AddId(PolicySql.BucketId, bucketId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async ValueTask<bool> SetBucketPolicyAsync(Guid bucketId, string document, DateTime updatedAt, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            if (!await BucketExistsAsync(connection, transaction, bucketId, ct))
                return false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PolicySql.Upsert;
            command.Parameters.AddId(PolicySql.BucketId, bucketId);
            command.Parameters.AddText(PolicySql.Document, document);
            command.Parameters.AddInt64(PolicySql.UpdatedAt, SqliteTime.ToUnixMs(updatedAt));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });
    }

    public async ValueTask DeleteBucketPolicyAsync(Guid bucketId, CancellationToken ct) =>
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PolicySql.Delete;
            command.Parameters.AddId(PolicySql.BucketId, bucketId);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });

    public async ValueTask<PolicyReplace> ReplaceBucketPolicyAsync(
        Guid bucketId, string? expected, string? document, DateTime updatedAt, CancellationToken ct) =>
        await writer.WriteAsync(async (connection, transaction) =>
        {
            if (!await BucketExistsAsync(connection, transaction, bucketId, ct))
                return PolicyReplace.BucketMissing;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = PolicySql.Find;
                find.Parameters.AddId(PolicySql.BucketId, bucketId);
                if (!string.Equals(await find.ExecuteScalarAsync(ct) as string, expected, StringComparison.Ordinal))
                    return PolicyReplace.Changed;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = document is null ? PolicySql.Delete : PolicySql.Upsert;
            command.Parameters.AddId(PolicySql.BucketId, bucketId);
            if (document is not null)
            {
                command.Parameters.AddText(PolicySql.Document, document);
                command.Parameters.AddInt64(PolicySql.UpdatedAt, SqliteTime.ToUnixMs(updatedAt));
            }

            await command.ExecuteNonQueryAsync(ct);
            return PolicyReplace.Replaced;
        });

    private static class PolicySql
    {
        public const string BucketId = "$bucketId";
        public const string Document = "$document";
        public const string UpdatedAt = "$updatedAt";

        public const string Find = "SELECT document FROM bucket_policies WHERE bucket_id = $bucketId;";

        public const string Upsert = """
            INSERT INTO bucket_policies (bucket_id, document, updated_at) VALUES ($bucketId, $document, $updatedAt)
            ON CONFLICT (bucket_id) DO UPDATE SET document = excluded.document, updated_at = excluded.updated_at;
            """;

        public const string Delete = "DELETE FROM bucket_policies WHERE bucket_id = $bucketId;";
    }
}
