using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TimeStore.Domain.Replication;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// Replication rules and the outbox. Object writes call <see cref="QueueChangeAsync"/> inside their own transaction;
/// with no rule at all that is one integer check (<see cref="_ruleCount"/>), no SQL.
/// </summary>
public sealed partial class SqliteMetadataStore : IReplicationStore
{
    private const string Pending = "pending";
    private const string Failed = "failed";
    private const string PutOperation = "put";
    private const string DeleteOperation = "delete";

    /// <summary>
    /// How many rules exist, or -1 when unknown (recounted by the next write). Read and written only inside writer
    /// delegates, which run one at a time; a rule write sets it to -1 before anything else in its round can queue.
    /// </summary>
    private int _ruleCount = -1;

    private readonly Channel<bool> _replicationSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>
    /// Queues a change of <paramref name="key"/> for the bucket's rule, when it has one covering the key (deletes only
    /// when the rule sends them). Runs in the caller's transaction; returns the rows written.
    /// </summary>
    private async ValueTask<int> QueueChangeAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid bucketId, string key,
        ReplicationOperation operation, DateTime at, CancellationToken ct)
    {
        if (_ruleCount < 0)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = ReplicationSql.CountRules;
            _ruleCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        if (_ruleCount == 0)
            return 0;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReplicationSql.QueueChange;
        command.Parameters.AddId(ReplicationSql.BucketId, bucketId);
        command.Parameters.AddText(ReplicationSql.Key, key);
        command.Parameters.AddText(ReplicationSql.Operation, operation == ReplicationOperation.Put ? PutOperation : DeleteOperation);
        command.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(at));
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Called after a write committed: wakes the replication worker when it queued something.</summary>
    private void SignalReplication(int queued)
    {
        if (queued > 0)
            _replicationSignal.Writer.TryWrite(true);
    }

    public ValueTask WaitForChangesAsync(CancellationToken ct) =>
        new(_replicationSignal.Reader.ReadAsync(ct).AsTask());

    public async ValueTask<bool> SaveRuleAsync(ReplicationRule rule, bool queueExisting, CancellationToken ct)
    {
        var queued = -1;
        var saved = await writer.WriteAsync(async (connection, transaction) =>
        {
            _ruleCount = -1;
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = ReplicationSql.BucketExists;
                exists.Parameters.AddId(ReplicationSql.BucketId, rule.BucketId);
                if (await exists.ExecuteScalarAsync(ct) is null)
                    return false;
            }

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = ReplicationSql.Upsert;
                upsert.Parameters.AddId(ReplicationSql.Id, rule.Id);
                upsert.Parameters.AddId(ReplicationSql.BucketId, rule.BucketId);
                upsert.Parameters.AddText(ReplicationSql.Endpoint, rule.Endpoint);
                upsert.Parameters.AddText(ReplicationSql.TargetBucket, rule.TargetBucket);
                upsert.Parameters.AddText(ReplicationSql.Region, rule.Region);
                upsert.Parameters.AddText(ReplicationSql.AccessKeyId, rule.AccessKeyId);
                upsert.Parameters.AddNullableBlob(ReplicationSql.Secret, rule.SecretCiphertext);
                upsert.Parameters.AddText(ReplicationSql.Prefix, rule.Prefix);
                upsert.Parameters.AddInt64(ReplicationSql.ReplicateDeletes, rule.ReplicateDeletes ? 1 : 0);
                upsert.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(rule.CreatedAt));
                upsert.Parameters.AddInt64(ReplicationSql.UpdatedAt, SqliteTime.ToUnixMs(rule.UpdatedAt));
                await upsert.ExecuteNonQueryAsync(ct);
            }

            // New settings are a retry: what failed with the old ones is tried again.
            queued = await ExecuteAsync(connection, transaction, ReplicationSql.RetryFailedOfBucket, rule.BucketId, rule.UpdatedAt, ct);
            if (queueExisting)
                queued += await ExecuteAsync(connection, transaction, ReplicationSql.QueueExisting, rule.BucketId, rule.UpdatedAt, ct);
            return true;
        });
        SignalReplication(queued);
        return saved;
    }

    private static async ValueTask<int> ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, Guid bucketId, DateTime at, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddId(ReplicationSql.BucketId, bucketId);
        command.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(at));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<int> ResyncAsync(Guid bucketId, DateTime now, CancellationToken ct)
    {
        var queued = await writer.WriteAsync((connection, transaction) =>
            ExecuteAsync(connection, transaction, ReplicationSql.QueueExisting, bucketId, now, ct));
        SignalReplication(queued);
        return queued;
    }

    public async ValueTask<ReplicationRule?> FindRuleAsync(Guid bucketId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReplicationSql.SelectRules + " WHERE bucket_id = $bucketId;";
        command.Parameters.AddId(ReplicationSql.BucketId, bucketId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReplicationRule(reader) : null;
    }

    public async ValueTask<IReadOnlyList<ReplicationRule>> ListRulesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReplicationSql.SelectRules + " ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rules = new List<ReplicationRule>();
        while (await reader.ReadAsync(ct))
            rules.Add(ReadReplicationRule(reader));
        return rules;
    }

    public async ValueTask<bool> DeleteRuleAsync(Guid bucketId, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            _ruleCount = -1;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ReplicationSql.DeleteRule; // the queue cascades
            command.Parameters.AddId(ReplicationSql.BucketId, bucketId);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        });
    }

    public async ValueTask<IReadOnlyList<ReplicationChange>> ListDueChangesAsync(DateTime now, int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReplicationSql.Due;
        command.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(now));
        command.Parameters.AddInt64(ReplicationSql.Limit, limit);
        return await ReadChangesAsync(command, ct);
    }

    public async ValueTask<DateTime?> NextDueAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = ReplicationSql.NextDue;
        return await command.ExecuteScalarAsync(ct) is long ms ? SqliteTime.FromUnixMs(ms) : null;
    }

    public async ValueTask CompleteAsync(Guid ruleId, string key, long upToId, DateTime at, CancellationToken ct) =>
        await WriteKeyAsync(ReplicationSql.Complete, ruleId, key, upToId, at, error: null, ct);

    public async ValueTask DeferAsync(Guid ruleId, string key, long upToId, DateTime nextAttempt, string error, CancellationToken ct) =>
        await WriteKeyAsync(ReplicationSql.Defer, ruleId, key, upToId, nextAttempt, error, ct);

    public async ValueTask FailAsync(Guid ruleId, string key, long upToId, string error, CancellationToken ct) =>
        await WriteKeyAsync(ReplicationSql.Fail, ruleId, key, upToId, DateTime.UnixEpoch, error, ct);

    private async ValueTask WriteKeyAsync(
        string sql, Guid ruleId, string key, long upToId, DateTime at, string? error, CancellationToken ct)
    {
        await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddId(ReplicationSql.RuleId, ruleId);
            command.Parameters.AddText(ReplicationSql.Key, key);
            command.Parameters.AddInt64(ReplicationSql.UpTo, upToId);
            command.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(at));
            command.Parameters.AddNullableText(ReplicationSql.Error, error);
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

    public async ValueTask PauseRuleAsync(Guid ruleId, DateTime until, string error, CancellationToken ct) =>
        await WriteRuleAsync(ReplicationSql.Pause, ruleId, until, error, ct);

    public async ValueTask FailRuleAsync(Guid ruleId, string error, CancellationToken ct) =>
        await WriteRuleAsync(ReplicationSql.FailRule, ruleId, DateTime.UnixEpoch, error, ct);

    public async ValueTask<int> RetryAsync(Guid ruleId, DateTime now, CancellationToken ct)
    {
        var retried = await WriteRuleAsync(ReplicationSql.Retry, ruleId, now, error: null, ct);
        _replicationSignal.Writer.TryWrite(true);
        return retried;
    }

    private async ValueTask<int> WriteRuleAsync(string sql, Guid ruleId, DateTime at, string? error, CancellationToken ct)
    {
        return await writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddId(ReplicationSql.RuleId, ruleId);
            command.Parameters.AddInt64(ReplicationSql.At, SqliteTime.ToUnixMs(at));
            command.Parameters.AddNullableText(ReplicationSql.Error, error);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var changed = 0;
            do
            {
                // The retry statement ends with SELECT changes() of the failed rows it reset.
                if (await reader.ReadAsync(ct))
                    changed = reader.GetInt32(0);
            }
            while (await reader.NextResultAsync(ct));
            return changed;
        });
    }

    public async ValueTask<ReplicationBacklog> GetBacklogAsync(Guid ruleId, int maxFailures, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        long pending = 0, failed = 0;
        DateTime? oldest = null;
        await using (var counts = connection.CreateCommand())
        {
            counts.CommandText = ReplicationSql.Counts;
            counts.Parameters.AddId(ReplicationSql.RuleId, ruleId);
            await using var reader = await counts.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                pending = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                oldest = reader.IsDBNull(1) ? null : SqliteTime.FromUnixMs(reader.GetInt64(1));
                failed = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            }
        }

        await using var list = connection.CreateCommand();
        list.CommandText = ReplicationSql.Failures;
        list.Parameters.AddId(ReplicationSql.RuleId, ruleId);
        list.Parameters.AddInt64(ReplicationSql.Limit, maxFailures);
        return new ReplicationBacklog(pending, oldest, failed, await ReadChangesAsync(list, ct));
    }

    private static async ValueTask<IReadOnlyList<ReplicationChange>> ReadChangesAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var changes = new List<ReplicationChange>();
        while (await reader.ReadAsync(ct))
        {
            changes.Add(new ReplicationChange(
                reader.GetInt64(0), reader.GetId(1), reader.GetString(2),
                reader.GetString(3) == PutOperation ? ReplicationOperation.Put : ReplicationOperation.Delete,
                SqliteTime.FromUnixMs(reader.GetInt64(4)), reader.GetInt32(5), SqliteTime.FromUnixMs(reader.GetInt64(6)),
                reader.GetString(7) == Pending ? ReplicationChangeStatus.Pending : ReplicationChangeStatus.Failed,
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return changes;
    }

    private static ReplicationRule ReadReplicationRule(SqliteDataReader reader) =>
        new(reader.GetId(0), reader.GetId(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetFieldValue<byte[]>(6), reader.GetString(7), reader.GetInt64(8) != 0,
            SqliteTime.FromUnixMs(reader.GetInt64(9)), SqliteTime.FromUnixMs(reader.GetInt64(10)))
        {
            PausedUntil = SqliteTime.FromUnixMs(reader.GetInt64(11)),
            Failures = reader.GetInt32(12),
            LastError = reader.IsDBNull(13) ? null : reader.GetString(13),
            LastSuccessAt = reader.IsDBNull(14) ? null : SqliteTime.FromUnixMs(reader.GetInt64(14)),
        };

    private static class ReplicationSql
    {
        public const string Id = "$id";
        public const string RuleId = "$ruleId";
        public const string BucketId = "$bucketId";
        public const string Key = "$key";
        public const string Operation = "$operation";
        public const string At = "$at";
        public const string UpdatedAt = "$updatedAt";
        public const string Endpoint = "$endpoint";
        public const string TargetBucket = "$targetBucket";
        public const string Region = "$region";
        public const string AccessKeyId = "$accessKeyId";
        public const string Secret = "$secret";
        public const string Prefix = "$prefix";
        public const string ReplicateDeletes = "$replicateDeletes";
        public const string UpTo = "$upTo";
        public const string Error = "$error";
        public const string Limit = "$limit";

        public const string CountRules = "SELECT COUNT(*) FROM replication_rules;";
        public const string BucketExists = "SELECT 1 FROM buckets WHERE id = $bucketId;";

        // substr compares characters: an ordinal prefix match (LIKE would treat % and _ as wildcards and fold case).
        public const string QueueChange = $"""
            INSERT INTO replication_queue (rule_id, object_key, operation, created_at, next_attempt, status)
            SELECT r.id, $key, $operation, $at, $at, '{Pending}' FROM replication_rules r
            WHERE r.bucket_id = $bucketId AND substr($key, 1, length(r.prefix)) = r.prefix
              AND ($operation = '{PutOperation}' OR r.replicate_deletes = 1);
            """;

        public const string QueueExisting = $"""
            INSERT INTO replication_queue (rule_id, object_key, operation, created_at, next_attempt, status)
            SELECT r.id, o.key, '{PutOperation}', $at, $at, '{Pending}'
            FROM objects o JOIN replication_rules r ON r.bucket_id = o.bucket_id
            WHERE o.bucket_id = $bucketId AND substr(o.key, 1, length(r.prefix)) = r.prefix
            ORDER BY o.key;
            """;

        public const string RetryFailedOfBucket = $"""
            UPDATE replication_queue SET status = '{Pending}', attempts = 0, next_attempt = $at, last_error = NULL
            WHERE status = '{Failed}' AND rule_id = (SELECT id FROM replication_rules WHERE bucket_id = $bucketId);
            """;

        public const string Upsert = """
            INSERT INTO replication_rules (id, bucket_id, endpoint, target_bucket, region, access_key_id, secret_ciphertext,
                prefix, replicate_deletes, created_at, updated_at)
            VALUES ($id, $bucketId, $endpoint, $targetBucket, $region, $accessKeyId, $secret, $prefix, $replicateDeletes,
                $at, $updatedAt)
            ON CONFLICT (bucket_id) DO UPDATE SET endpoint = excluded.endpoint, target_bucket = excluded.target_bucket,
                region = excluded.region, access_key_id = excluded.access_key_id,
                secret_ciphertext = excluded.secret_ciphertext, prefix = excluded.prefix,
                replicate_deletes = excluded.replicate_deletes, updated_at = excluded.updated_at, paused_until = 0,
                failures = 0, last_error = NULL;
            """;

        // Column order is read by ReadReplicationRule.
        public const string SelectRules = """
            SELECT id, bucket_id, endpoint, target_bucket, region, access_key_id, secret_ciphertext, prefix,
                replicate_deletes, created_at, updated_at, paused_until, failures, last_error, last_success_at
            FROM replication_rules
            """;

        public const string DeleteRule = "DELETE FROM replication_rules WHERE bucket_id = $bucketId;";

        // Column order is read by ReadChangesAsync.
        private const string ChangeColumns =
            "q.id, q.rule_id, q.object_key, q.operation, q.created_at, q.attempts, q.next_attempt, q.status, q.last_error";

        public const string Due = $"""
            SELECT {ChangeColumns} FROM replication_queue q JOIN replication_rules r ON r.id = q.rule_id
            WHERE q.status = '{Pending}' AND q.next_attempt <= $at AND r.paused_until <= $at
            ORDER BY q.id LIMIT $limit;
            """;

        public const string NextDue = $"""
            SELECT MIN(MAX(q.next_attempt, r.paused_until)) FROM replication_queue q
            JOIN replication_rules r ON r.id = q.rule_id WHERE q.status = '{Pending}';
            """;

        // Every change of the key up to the one the worker read is done, failed ones included: the current state is sent.
        public const string Complete = """
            DELETE FROM replication_queue WHERE rule_id = $ruleId AND object_key = $key AND id <= $upTo;
            UPDATE replication_rules SET failures = 0, paused_until = 0, last_error = NULL, last_success_at = $at
            WHERE id = $ruleId;
            """;

        public const string Defer = $"""
            UPDATE replication_queue SET attempts = attempts + 1, next_attempt = $at, last_error = $error
            WHERE rule_id = $ruleId AND object_key = $key AND id <= $upTo AND status = '{Pending}';
            """;

        public const string Fail = $"""
            UPDATE replication_queue SET status = '{Failed}', attempts = attempts + 1, last_error = $error
            WHERE rule_id = $ruleId AND object_key = $key AND id <= $upTo AND status = '{Pending}';
            """;

        public const string Pause = """
            UPDATE replication_rules SET paused_until = $at, failures = failures + 1, last_error = $error WHERE id = $ruleId;
            """;

        public const string FailRule = $"""
            UPDATE replication_queue SET status = '{Failed}', attempts = attempts + 1, last_error = $error
            WHERE rule_id = $ruleId AND status = '{Pending}';
            UPDATE replication_rules SET paused_until = 0, failures = 0, last_error = $error WHERE id = $ruleId;
            """;

        public const string Retry = $"""
            UPDATE replication_rules SET paused_until = 0, failures = 0, last_error = NULL WHERE id = $ruleId;
            UPDATE replication_queue SET next_attempt = $at WHERE rule_id = $ruleId AND status = '{Pending}' AND next_attempt > $at;
            UPDATE replication_queue SET status = '{Pending}', attempts = 0, next_attempt = $at, last_error = NULL
            WHERE rule_id = $ruleId AND status = '{Failed}';
            SELECT changes();
            """;

        public const string Counts = $"""
            SELECT SUM(status = '{Pending}'), MIN(CASE WHEN status = '{Pending}' THEN created_at END), SUM(status = '{Failed}')
            FROM replication_queue WHERE rule_id = $ruleId;
            """;

        public const string Failures = $"""
            SELECT {ChangeColumns} FROM replication_queue q
            WHERE q.rule_id = $ruleId AND q.status = '{Failed}' ORDER BY q.id DESC LIMIT $limit;
            """;
    }
}
