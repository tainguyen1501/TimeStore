using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Replication;
using TimeStore.Contracts.Replication;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Replication;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Replication;

public sealed class ReplicationService(
    IMetadataStore metadata, IReplicationStore store, IReplicationClient client, ISecretProtector protector,
    ReplicationOptions options, TimeProvider clock) : IReplicationService
{
    /// <summary>Failed changes listed in a response, newest first.</summary>
    public const int MaxFailuresShown = 100;

    private const string PutText = "put";
    private const string DeleteText = "delete";

    public async ValueTask<Result<ReplicationResponse>> GetAsync(string bucket, CancellationToken ct)
    {
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.FindRuleAsync(found.Id, ct) is not { } rule)
            return ReplicationErrors.NotFound(bucket);
        return await ToResponseAsync(bucket, rule, ct);
    }

    public async ValueTask<Result<ReplicationResponse>> SetAsync(string bucket, SetReplicationRequest request, CancellationToken ct)
    {
        if (!options.Enabled)
            return ReplicationErrors.Disabled;
        var validated = ReplicationValidation.Validate(request);
        if (validated.IsFailure)
            return validated.Error;
        var valid = validated.Value;
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);

        var existing = await store.FindRuleAsync(found.Id, ct);
        var endpoint = valid.Endpoint.GetLeftPart(UriPartial.Authority);
        var secret = valid.SecretKey;
        if (secret is null)
        {
            // Keeping the stored secret is allowed only for the same endpoint and access key.
            if (existing is null || existing.Endpoint != endpoint || existing.AccessKeyId != valid.AccessKeyId
                || ReplicationSecrets.Destination(protector, existing) is not { } stored)
                return ReplicationErrors.SecretRequired;
            secret = stored.SecretKey;
        }

        var destination = new ReplicationDestination(valid.Endpoint, valid.Bucket, valid.Region, valid.AccessKeyId, secret);
        var test = await client.TestAsync(destination, ct);
        if (!test.IsSuccess)
            return ReplicationErrors.DestinationFailed(test.Error ?? test.Failure.ToString());

        var now = clock.UtcNowMs;
        var rule = new ReplicationRule(
            existing?.Id ?? Guid.CreateVersion7(now), found.Id, endpoint, valid.Bucket, valid.Region, valid.AccessKeyId,
            ReplicationSecrets.Protect(protector, found.Id, secret), valid.Prefix, request.ReplicateDeletes,
            existing?.CreatedAt ?? now, now);
        // Existing objects are queued for a new rule or a new destination or prefix, not for every edit (toggling
        // replicateDeletes must not send the whole bucket again; resending everything is a resync, R2).
        var queueExisting = request.ExistingObjects && (existing is null || existing.Endpoint != endpoint
            || existing.TargetBucket != valid.Bucket || existing.Prefix != valid.Prefix);
        if (!await store.SaveRuleAsync(rule, queueExisting, ct))
            return BucketErrors.NotFound(bucket);
        return await GetAsync(bucket, ct);
    }

    public async ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct)
    {
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        return await store.DeleteRuleAsync(found.Id, ct) ? Result.Success : ReplicationErrors.NotFound(bucket);
    }

    public async ValueTask<Result<ReplicationResponse>> TestAsync(string bucket, CancellationToken ct)
    {
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.FindRuleAsync(found.Id, ct) is not { } rule)
            return ReplicationErrors.NotFound(bucket);
        if (ReplicationSecrets.Destination(protector, rule) is not { } destination)
            return ReplicationErrors.DestinationFailed(ReplicationRunner.UnreadableSecret);
        var test = await client.TestAsync(destination, ct);
        return test.IsSuccess
            ? await ToResponseAsync(bucket, rule, ct)
            : ReplicationErrors.DestinationFailed(test.Error ?? test.Failure.ToString());
    }

    public async ValueTask<Result<ReplicationResponse>> RetryAsync(string bucket, CancellationToken ct)
    {
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.FindRuleAsync(found.Id, ct) is not { } rule)
            return ReplicationErrors.NotFound(bucket);
        await store.RetryAsync(rule.Id, clock.UtcNowMs, ct);
        return await GetAsync(bucket, ct);
    }

    public async ValueTask<Result<ReplicationResponse>> ResyncAsync(string bucket, CancellationToken ct)
    {
        if (await metadata.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (await store.FindRuleAsync(found.Id, ct) is null)
            return ReplicationErrors.NotFound(bucket);
        await store.ResyncAsync(found.Id, clock.UtcNowMs, ct);
        return await GetAsync(bucket, ct);
    }

    public async ValueTask<IReadOnlyList<ReplicationResponse>> ListAsync(CancellationToken ct)
    {
        var rules = await store.ListRulesAsync(ct);
        if (rules.Count == 0)
            return [];
        var names = (await metadata.ListBucketsAsync(ct)).ToDictionary(static b => b.Id, static b => b.Name);
        var responses = new List<ReplicationResponse>(rules.Count);
        foreach (var rule in rules)
        {
            if (names.TryGetValue(rule.BucketId, out var name))
                responses.Add((await ToResponseAsync(name, rule, ct, maxFailures: 0)).Value);
        }

        return responses.OrderBy(static r => r.SourceBucket, StringComparer.Ordinal).ToArray();
    }

    private async ValueTask<Result<ReplicationResponse>> ToResponseAsync(
        string bucket, ReplicationRule rule, CancellationToken ct, int maxFailures = MaxFailuresShown)
    {
        var backlog = await store.GetBacklogAsync(rule.Id, maxFailures, ct);
        var failures = backlog.Failures
            .Select(static f => new ReplicationFailureResponse(
                f.Key, f.Operation == ReplicationOperation.Put ? PutText : DeleteText, f.Attempts, f.LastError, f.CreatedAt))
            .ToArray();
        return new ReplicationResponse(
            rule.Id, bucket, rule.Endpoint, rule.TargetBucket, rule.Region, rule.AccessKeyId, rule.Prefix, rule.ReplicateDeletes,
            rule.CreatedAt, rule.UpdatedAt, rule.PausedUntil > clock.UtcNowMs ? rule.PausedUntil : null, rule.LastError,
            rule.LastSuccessAt, new ReplicationBacklogResponse(backlog.Pending, backlog.OldestPendingAt, backlog.Failed, failures));
    }
}
