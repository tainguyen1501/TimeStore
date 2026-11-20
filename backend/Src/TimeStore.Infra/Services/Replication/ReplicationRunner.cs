using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Replication;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Replication;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Replication;

/// <summary>
/// Sends queued changes (docs/research/replication.md "The worker"). Up to <c>Concurrency</c> keys at a time; a slow
/// object never holds back the others. For each key it reads the key's <b>current</b> state and sends that once, then
/// deletes the key's changes up to the newest one it had read: changes made during the send stay queued and are sent
/// next (coalescing, never a lost change). Woken by commits through <see cref="IReplicationStore.WaitForChangesAsync"/>;
/// the poll interval is only a safety net.
/// </summary>
public sealed class ReplicationRunner(
    IReplicationStore store, IMetadataStore metadata, IBlobStore blobs, IReplicationClient client,
    ISecretProtector protector, ReplicationOptions options, TimeProvider clock, IReplicationObserver? observer = null)
    : IReplicationRunner
{
    /// <summary>Changes read per pass (several per key are coalesced, so this is more than the keys sent).</summary>
    public const int ReadLimit = 1000;

    private static readonly TimeSpan MinIdle = TimeSpan.FromMilliseconds(100);

    public const string UnreadableSecret = "the stored secret key does not decrypt with this master key; save the rule again";

    public async Task RunAsync(CancellationToken ct)
    {
        var inFlight = new Dictionary<(Guid RuleId, string Key), Task>();
        var wake = store.WaitForChangesAsync(ct).AsTask();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = await StartDueAsync(inFlight, ct);
                if (inFlight.Count > 0)
                {
                    // Busy: continue when a key is done (a slot is free) or new changes arrive.
                    var done = await Task.WhenAny([.. inFlight.Values, wake]);
                    foreach (var (key, task) in inFlight.Where(static p => p.Value.IsCompleted).ToArray())
                    {
                        inFlight.Remove(key);
                        await task; // never throws: SendAsync records every failure
                    }

                    if (done == wake)
                        wake = store.WaitForChangesAsync(ct).AsTask();
                    continue;
                }

                if (started > 0)
                    continue;

                // Idle: sleep until a commit wakes us, the next change is due, or the poll interval passes.
                var next = await store.NextDueAsync(ct);
                var delay = options.PollInterval;
                if (next is { } due && due - clock.UtcNowMs < delay)
                    delay = due - clock.UtcNowMs;
                if (delay < MinIdle)
                    delay = MinIdle; // a commit still wakes at once; this only keeps a stray due row from spinning
                await Task.WhenAny(wake, Task.Delay(delay, clock, ct));
                if (wake.IsCompleted)
                {
                    wake = store.WaitForChangesAsync(ct).AsTask();
                    if (options.BatchDelay > TimeSpan.Zero)
                        await Task.Delay(options.BatchDelay, clock, ct); // let a burst of writes to one key coalesce
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }

        // Let sends in progress record their outcome (they stop at once on the cancelled token).
        await Task.WhenAll(inFlight.Values);
    }

    /// <summary>Starts sends for due keys while slots are free; returns how many started.</summary>
    internal async Task<int> StartDueAsync(Dictionary<(Guid RuleId, string Key), Task> inFlight, CancellationToken ct)
    {
        if (inFlight.Count >= options.Concurrency)
            return 0;
        var changes = await store.ListDueChangesAsync(clock.UtcNowMs, ReadLimit, ct);
        if (changes.Count == 0)
            return 0;

        var rules = (await store.ListRulesAsync(ct)).ToDictionary(static r => r.Id);
        var started = 0;
        // Oldest first; per key the newest change read is the bound of what the send completes.
        foreach (var group in changes.GroupBy(static c => (c.RuleId, c.Key)))
        {
            if (inFlight.Count >= options.Concurrency)
                break;
            if (inFlight.ContainsKey(group.Key) || !rules.TryGetValue(group.Key.RuleId, out var rule))
                continue;
            var upToId = group.Max(static c => c.Id);
            var attempts = group.Max(static c => c.Attempts);
            inFlight[group.Key] = SendAsync(rule, group.Key.Key, upToId, attempts, ct);
            started++;
        }

        return started;
    }

    /// <summary>Sends one key's current state and records the outcome; never throws (except on shutdown).</summary>
    internal async Task SendAsync(ReplicationRule rule, string key, long upToId, int attempts, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            var outcome = await TrySendAsync(rule, key, ct);
            var now = clock.UtcNowMs;
            switch (outcome.Failure)
            {
                case ReplicationFailure.None:
                    await store.CompleteAsync(rule.Id, key, upToId, now, CancellationToken.None);
                    if (rule.Failures > 0)
                        observer?.Resumed(rule.Id, rule.Failures);
                    return;
                case ReplicationFailure.Temporary:
                    await store.PauseRuleAsync(rule.Id, now + options.Backoff(rule.Failures + 1), outcome.Error!, CancellationToken.None);
                    break;
                case ReplicationFailure.DestinationRejected:
                    await store.FailRuleAsync(rule.Id, outcome.Error!, CancellationToken.None);
                    break;
                case ReplicationFailure.ObjectRejected:
                    await store.FailAsync(rule.Id, key, upToId, outcome.Error!, CancellationToken.None);
                    break;
                case ReplicationFailure.SourceUnavailable:
                    await store.DeferAsync(rule.Id, key, upToId, now + options.Backoff(attempts + 1), outcome.Error!, CancellationToken.None);
                    break;
            }

            observer?.Failed(rule.Id, key, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down: the changes stay pending and are sent after the restart
        }
    }

    private async ValueTask<ReplicationOutcome> TrySendAsync(ReplicationRule rule, string key, CancellationToken ct)
    {
        if (ReplicationSecrets.Destination(protector, rule) is not { } destination)
            return new ReplicationOutcome(ReplicationFailure.DestinationRejected, UnreadableSecret);
        try
        {
            var current = await metadata.FindObjectAsync(rule.BucketId, key, ct);
            if (current is null)
            {
                // Gone (deleted, expired, or the newest version removed). Without replicateDeletes the destination keeps it.
                return rule.ReplicateDeletes ? await client.DeleteAsync(destination, key, ct) : ReplicationOutcome.Success;
            }

            return await client.PutAsync(destination, current, OpenBlobAsync, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unexpected (a bug, the metadata unreadable): never lose the change, try the key again later.
            return new ReplicationOutcome(ReplicationFailure.SourceUnavailable, exception.GetType().Name);
        }
    }

    private async ValueTask<Stream> OpenBlobAsync(BlobLocator blob, CancellationToken ct)
    {
        try
        {
            return await blobs.OpenReadAsync(blob, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Released by a newer commit and deleted after its grace period: the next try sends the newer state.
            throw new ReplicationSourceException($"blob {blob.BlobId} could not be read ({exception.GetType().Name})", exception);
        }
    }
}
