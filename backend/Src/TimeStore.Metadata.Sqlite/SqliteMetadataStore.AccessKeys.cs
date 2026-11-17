using Microsoft.Data.Sqlite;
using TimeStore.Domain.AccessKeys;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    public async ValueTask<bool> TryCreateAccessKeyAsync(AccessKey key, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AccessKeySql.InsertIfFree;
            command.Parameters.AddText(AccessKeySql.Id, key.AccessKeyId);
            command.Parameters.AddNullableBlob(AccessKeySql.Secret, key.SecretCiphertext);
            command.Parameters.AddText(AccessKeySql.Description, key.Description);
            command.Parameters.AddText(AccessKeySql.Role, key.Role);
            command.Parameters.AddText(AccessKeySql.Scope, key.S3Scope);
            command.Parameters.AddInt64(AccessKeySql.CreatedAt, SqliteTime.ToUnixMs(key.CreatedAt));
            command.Parameters.AddNullableInt64(AccessKeySql.DisabledAt, key.DisabledAt is { } at ? SqliteTime.ToUnixMs(at) : null);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<AccessKey?> FindAccessKeyAsync(string accessKeyId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = AccessKeySql.Find;
        command.Parameters.AddText(AccessKeySql.Id, accessKeyId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAccessKey(reader) : null;
    }

    public async ValueTask<IReadOnlyList<AccessKey>> ListAccessKeysAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = AccessKeySql.List;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var keys = new List<AccessKey>();
        while (await reader.ReadAsync(ct))
            keys.Add(ReadAccessKey(reader));
        return keys;
    }

    public async ValueTask<bool> SetAccessKeyDisabledAsync(string accessKeyId, DateTime? disabledAt, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AccessKeySql.SetDisabled;
            command.Parameters.AddText(AccessKeySql.Id, accessKeyId);
            command.Parameters.AddNullableInt64(AccessKeySql.DisabledAt, disabledAt is { } at ? SqliteTime.ToUnixMs(at) : null);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<bool> DeleteAccessKeyAsync(string accessKeyId, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AccessKeySql.Delete;
            command.Parameters.AddText(AccessKeySql.Id, accessKeyId);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<bool> SetAccessKeyRoleAsync(string accessKeyId, string role, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AccessKeySql.SetRole;
            command.Parameters.AddText(AccessKeySql.Id, accessKeyId);
            command.Parameters.AddText(AccessKeySql.Role, role);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<bool> SetAccessKeyScopeAsync(string accessKeyId, string scope, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AccessKeySql.SetScope;
            command.Parameters.AddText(AccessKeySql.Id, accessKeyId);
            command.Parameters.AddText(AccessKeySql.Scope, scope);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    private static AccessKey ReadAccessKey(SqliteDataReader reader) => new(
        AccessKeyId: reader.GetString(0),
        SecretCiphertext: reader.GetFieldValue<byte[]>(1),
        Description: reader.GetString(2),
        Role: reader.GetString(5),
        CreatedAt: SqliteTime.FromUnixMs(reader.GetInt64(3)),
        DisabledAt: reader.IsDBNull(4) ? null : SqliteTime.FromUnixMs(reader.GetInt64(4)))
    {
        S3Scope = reader.GetString(6),
    };

    private static class AccessKeySql
    {
        public const string Id = "$id";
        public const string Secret = "$secret";
        public const string Description = "$description";
        public const string CreatedAt = "$createdAt";
        public const string DisabledAt = "$disabledAt";
        public const string Role = "$role";
        public const string Scope = "$scope";

        // Column order is read by ReadAccessKey.
        private const string Columns = "access_key_id, secret_ciphertext, description, created_at, disabled_at, role, s3_scope";

        public const string InsertIfFree = $"""
            INSERT INTO access_keys ({Columns}) VALUES ($id, $secret, $description, $createdAt, $disabledAt, $role, $scope)
            ON CONFLICT (access_key_id) DO NOTHING;
            """;

        public const string Find = $"SELECT {Columns} FROM access_keys WHERE access_key_id = $id;";
        public const string List = $"SELECT {Columns} FROM access_keys ORDER BY created_at, access_key_id;";
        public const string SetDisabled = "UPDATE access_keys SET disabled_at = $disabledAt WHERE access_key_id = $id;";
        public const string Delete = "DELETE FROM access_keys WHERE access_key_id = $id;";
        public const string SetRole = "UPDATE access_keys SET role = $role WHERE access_key_id = $id;";
        public const string SetScope = "UPDATE access_keys SET s3_scope = $scope WHERE access_key_id = $id;";
    }
}
