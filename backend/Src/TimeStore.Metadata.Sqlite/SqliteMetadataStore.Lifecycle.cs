using Microsoft.Data.Sqlite;
using TimeStore.Domain.Lifecycle;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    public async ValueTask<IReadOnlyList<LifecycleRule>?> GetLifecycleAsync(Guid bucketId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = LifecycleSql.Rules;
        command.Parameters.AddId(LifecycleSql.BucketId, bucketId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rules = new List<LifecycleRule>();
        while (await reader.ReadAsync(ct))
            rules.Add(ReadRule(reader));
        return rules.Count == 0 ? null : rules;
    }

    public async ValueTask<bool> SetLifecycleAsync(Guid bucketId, IReadOnlyList<LifecycleRule> rules, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            if (!await BucketExistsAsync(connection, transaction, bucketId, ct))
                return false;
            await DeleteRulesAsync(connection, transaction, bucketId, ct);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = LifecycleSql.Insert;
            insert.Parameters.AddId(LifecycleSql.BucketId, bucketId);
            var position = insert.Parameters.Add(LifecycleSql.Position, SqliteType.Integer);
            var id = insert.Parameters.Add(LifecycleSql.RuleId, SqliteType.Text);
            var enabled = insert.Parameters.Add(LifecycleSql.Enabled, SqliteType.Integer);
            var prefix = insert.Parameters.Add(LifecycleSql.Prefix, SqliteType.Text);
            var inRule = insert.Parameters.Add(LifecycleSql.PrefixInRule, SqliteType.Integer);
            var greater = insert.Parameters.Add(LifecycleSql.SizeGreaterThan, SqliteType.Integer);
            var less = insert.Parameters.Add(LifecycleSql.SizeLessThan, SqliteType.Integer);
            var days = insert.Parameters.Add(LifecycleSql.ExpirationDays, SqliteType.Integer);
            var date = insert.Parameters.Add(LifecycleSql.ExpirationDate, SqliteType.Integer);
            var markers = insert.Parameters.Add(LifecycleSql.ExpiredMarkers, SqliteType.Integer);
            var noncurrent = insert.Parameters.Add(LifecycleSql.NoncurrentDays, SqliteType.Integer);
            var newer = insert.Parameters.Add(LifecycleSql.NewerNoncurrent, SqliteType.Integer);
            var abort = insert.Parameters.Add(LifecycleSql.AbortDays, SqliteType.Integer);
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                position.Value = i;
                id.Value = rule.Id;
                enabled.Value = rule.Enabled ? 1 : 0;
                prefix.Value = rule.Filter.Prefix;
                inRule.Value = rule.Filter.PrefixInRule ? 1 : 0;
                greater.Value = (object?)rule.Filter.SizeGreaterThan ?? DBNull.Value;
                less.Value = (object?)rule.Filter.SizeLessThan ?? DBNull.Value;
                days.Value = (object?)rule.ExpirationDays ?? DBNull.Value;
                date.Value = rule.ExpirationDate is { } at ? SqliteTime.ToUnixMs(at) : DBNull.Value;
                markers.Value = rule.ExpiredObjectDeleteMarker ? 1 : 0;
                noncurrent.Value = (object?)rule.NoncurrentDays ?? DBNull.Value;
                newer.Value = (object?)rule.NewerNoncurrentVersions ?? DBNull.Value;
                abort.Value = (object?)rule.AbortIncompleteUploadDays ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(ct);
            }

            return true;
        });
    }

    public async ValueTask DeleteLifecycleAsync(Guid bucketId, CancellationToken ct) =>
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await DeleteRulesAsync(connection, transaction, bucketId, ct);
            return true;
        });

    public async ValueTask<IReadOnlyList<Guid>> ListBucketsWithLifecycleAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = LifecycleSql.BucketsWithRules;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetId(0));
        return ids;
    }

    private static async ValueTask DeleteRulesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, CancellationToken ct)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = LifecycleSql.Delete;
        delete.Parameters.AddId(LifecycleSql.BucketId, bucketId);
        await delete.ExecuteNonQueryAsync(ct);
    }

    private static LifecycleRule ReadRule(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Enabled: reader.GetInt64(1) != 0,
        Filter: new LifecycleFilter(
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(11) != 0),
        ExpirationDays: reader.IsDBNull(5) ? null : reader.GetInt32(5),
        ExpirationDate: reader.IsDBNull(6) ? null : SqliteTime.FromUnixMs(reader.GetInt64(6)),
        ExpiredObjectDeleteMarker: reader.GetInt64(7) != 0,
        NoncurrentDays: reader.IsDBNull(8) ? null : reader.GetInt32(8),
        NewerNoncurrentVersions: reader.IsDBNull(9) ? null : reader.GetInt32(9),
        AbortIncompleteUploadDays: reader.IsDBNull(10) ? null : reader.GetInt32(10));

    private static class LifecycleSql
    {
        public const string BucketId = "$bucketId";
        public const string Position = "$position";
        public const string RuleId = "$ruleId";
        public const string Enabled = "$enabled";
        public const string Prefix = "$prefix";
        public const string PrefixInRule = "$prefixInRule";
        public const string SizeGreaterThan = "$sizeGreaterThan";
        public const string SizeLessThan = "$sizeLessThan";
        public const string ExpirationDays = "$expirationDays";
        public const string ExpirationDate = "$expirationDate";
        public const string ExpiredMarkers = "$expiredMarkers";
        public const string NoncurrentDays = "$noncurrentDays";
        public const string NewerNoncurrent = "$newerNoncurrent";
        public const string AbortDays = "$abortDays";

        // Column order is read by ReadRule.
        public const string Rules = """
            SELECT rule_id, enabled, prefix, size_greater_than, size_less_than, expiration_days, expiration_date,
                   expired_object_delete_marker, noncurrent_days, newer_noncurrent_versions, abort_upload_days, prefix_in_rule
            FROM lifecycle_rules WHERE bucket_id = $bucketId ORDER BY position;
            """;

        public const string Insert = """
            INSERT INTO lifecycle_rules (bucket_id, position, rule_id, enabled, prefix, prefix_in_rule, size_greater_than, size_less_than,
                expiration_days, expiration_date, expired_object_delete_marker, noncurrent_days, newer_noncurrent_versions,
                abort_upload_days)
            VALUES ($bucketId, $position, $ruleId, $enabled, $prefix, $prefixInRule, $sizeGreaterThan, $sizeLessThan, $expirationDays,
                $expirationDate, $expiredMarkers, $noncurrentDays, $newerNoncurrent, $abortDays);
            """;

        public const string Delete = "DELETE FROM lifecycle_rules WHERE bucket_id = $bucketId;";

        public const string BucketsWithRules = "SELECT DISTINCT bucket_id FROM lifecycle_rules WHERE enabled = 1;";
    }
}
