using TimeStore.Domain.AccessKeys;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    public async ValueTask CreateSessionAsync(AdminSession session, CancellationToken ct)
    {
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SessionSql.Insert;
            command.Parameters.AddNullableBlob(SessionSql.TokenHash, session.TokenHash);
            command.Parameters.AddText(SessionSql.AccessKeyId, session.AccessKeyId);
            command.Parameters.AddInt64(SessionSql.CreatedAt, SqliteTime.ToUnixMs(session.CreatedAt));
            command.Parameters.AddInt64(SessionSql.ExpiresAt, SqliteTime.ToUnixMs(session.ExpiresAt));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });
    }

    public async ValueTask<AdminSession?> FindSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSql.Find;
        command.Parameters.AddNullableBlob(SessionSql.TokenHash, tokenHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new AdminSession(
            reader.GetFieldValue<byte[]>(0), reader.GetString(1),
            SqliteTime.FromUnixMs(reader.GetInt64(2)), SqliteTime.FromUnixMs(reader.GetInt64(3)));
    }

    public async ValueTask DeleteSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SessionSql.Delete;
            command.Parameters.AddNullableBlob(SessionSql.TokenHash, tokenHash);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });
    }

    public async ValueTask<int> DeleteSessionsOfAsync(string accessKeyId, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SessionSql.DeleteOfKey;
            command.Parameters.AddText(SessionSql.AccessKeyId, accessKeyId);
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

    public async ValueTask<int> DeleteExpiredSessionsAsync(DateTime now, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SessionSql.DeleteExpired;
            command.Parameters.AddInt64(SessionSql.Now, SqliteTime.ToUnixMs(now));
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

    private static class SessionSql
    {
        public const string TokenHash = "$tokenHash";
        public const string AccessKeyId = "$accessKeyId";
        public const string CreatedAt = "$createdAt";
        public const string ExpiresAt = "$expiresAt";
        public const string Now = "$now";

        public const string Insert = """
            INSERT INTO admin_sessions (token_hash, access_key_id, created_at, expires_at)
            VALUES ($tokenHash, $accessKeyId, $createdAt, $expiresAt);
            """;

        // Column order is read by FindSessionAsync.
        public const string Find =
            "SELECT token_hash, access_key_id, created_at, expires_at FROM admin_sessions WHERE token_hash = $tokenHash;";

        public const string Delete = "DELETE FROM admin_sessions WHERE token_hash = $tokenHash;";
        public const string DeleteOfKey = "DELETE FROM admin_sessions WHERE access_key_id = $accessKeyId;";
        public const string DeleteExpired = "DELETE FROM admin_sessions WHERE expires_at < $now;";
    }
}
