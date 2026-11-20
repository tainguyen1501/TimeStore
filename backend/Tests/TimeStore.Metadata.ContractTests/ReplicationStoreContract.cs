using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Replication;
using TimeStore.Domain.Stores;
using TimeStore.Domain.Uploads;

namespace TimeStore.Metadata.ContractTests;

/// <summary>
/// The behaviour every provider's <see cref="IReplicationStore"/> must have, and the outbox rows the object write
/// methods of <see cref="IMetadataStore"/> write in their own transaction.
/// </summary>
public abstract class ReplicationStoreContract : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Now.AddYears(1);
    private const string Md5 = "d41d8cd98f00b204e9800998ecf8427e";

    protected IMetadataStore Store { get; private set; } = null!;

    protected IReplicationStore Replication { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract ValueTask<(IMetadataStore Store, IReplicationStore Replication)> CreateAsync();

    public virtual async ValueTask InitializeAsync() => (Store, Replication) = await CreateAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task WithoutARule_NoWriteQueuesAnything()
    {
        var bucket = await CreateBucketAsync();
        await PutAsync(bucket, "a");
        await Store.DeleteObjectAsync(bucket.Id, "a", Ct);

        Assert.Empty(await Replication.ListDueChangesAsync(Later, 100, Ct));
        Assert.Null(await Replication.NextDueAsync(Ct));
    }

    [Fact]
    public async Task EveryWritePath_QueuesItsChange_InOrder()
    {
        var bucket = await CreateBucketAsync();
        await SaveRuleAsync(bucket, replicateDeletes: true);

        await PutAsync(bucket, "put");
        var upload = new MultipartUpload(Guid.CreateVersion7(), bucket.Id, "multipart", "video/mp4", null, null, [], [], Now);
        Assert.True(await Store.TryCreateUploadAsync(upload, Ct));
        var part = new UploadPart(upload.UploadId, 1, Blob(), null, Now);
        await Store.CommitPartAsync(part, Ct);
        var manifest = new MultipartBlobs(upload.UploadId, [new ObjectPart(1, 0, part.Blob, null)]);
        Assert.Equal(CompletionStatus.Completed, (await Store.CompleteUploadAsync(
            Object(bucket, "multipart") with { Data = manifest, ETag = Md5 + "-1" }, CommitCondition.Always, Ct)).Status);
        await Store.DeleteObjectAsync(bucket.Id, "one", Ct);
        await Store.DeleteObjectsAsync(bucket.Id, ["two", "three"], Ct);
        await Store.DeleteObjectVersionsAsync(bucket.Id, [new ObjectToDelete("four", VersionRef.Null)], Ct);

        var changes = await Replication.ListDueChangesAsync(Later, 100, Ct);
        Assert.Equal(["put", "multipart", "one", "two", "three", "four"], changes.Select(static c => c.Key));
        Assert.Equal(
            [ReplicationOperation.Put, ReplicationOperation.Put, ReplicationOperation.Delete, ReplicationOperation.Delete,
                ReplicationOperation.Delete, ReplicationOperation.Put], // one version deleted: the key's content may have changed
            changes.Select(static c => c.Operation));
        Assert.True(changes.Zip(changes.Skip(1)).All(static p => p.First.Id < p.Second.Id));
    }

    [Fact]
    public async Task OnlyKeysUnderThePrefix_AndDeletesOnlyWhenTheRuleSendsThem()
    {
        var bucket = await CreateBucketAsync();
        await SaveRuleAsync(bucket, prefix: "logs/", replicateDeletes: false);

        await PutAsync(bucket, "logs/a");
        await PutAsync(bucket, "img/b");
        await PutAsync(bucket, "logs%"); // not a wildcard: an ordinal prefix
        await Store.DeleteObjectAsync(bucket.Id, "logs/a", Ct);

        Assert.Equal(["logs/a"], (await Replication.ListDueChangesAsync(Later, 100, Ct)).Select(static c => c.Key));
    }

    [Fact]
    public async Task Complete_DeletesTheKeysChangesUpToTheOneRead_NewerOnesStay()
    {
        var bucket = await CreateBucketAsync();
        var rule = await SaveRuleAsync(bucket);
        await PutAsync(bucket, "a");
        await PutAsync(bucket, "a");
        await PutAsync(bucket, "b");
        var read = await Replication.ListDueChangesAsync(Later, 100, Ct);
        await PutAsync(bucket, "a"); // committed while the worker sends

        await Replication.CompleteAsync(rule.Id, "a", read.Where(static c => c.Key == "a").Max(static c => c.Id), Now, Ct);

        Assert.Equal(["b", "a"], (await Replication.ListDueChangesAsync(Later, 100, Ct)).Select(static c => c.Key));
        Assert.Equal(Now, (await Replication.FindRuleAsync(bucket.Id, Ct))!.LastSuccessAt);
    }

    [Fact]
    public async Task SaveRule_WithExistingObjects_QueuesThemInTheSameTransaction()
    {
        var bucket = await CreateBucketAsync();
        await PutAsync(bucket, "old/1");
        await PutAsync(bucket, "old/2");
        await PutAsync(bucket, "other");

        await SaveRuleAsync(bucket, prefix: "old/", queueExisting: true);

        Assert.Equal(["old/1", "old/2"], (await Replication.ListDueChangesAsync(Later, 100, Ct)).Select(static c => c.Key));
    }

    [Fact]
    public async Task SaveRule_Again_KeepsTheIdAndTheQueue_AndRetriesFailures()
    {
        var bucket = await CreateBucketAsync();
        var first = await SaveRuleAsync(bucket);
        await PutAsync(bucket, "a");
        await PutAsync(bucket, "b");
        await Replication.FailAsync(first.Id, "a", long.MaxValue, "AccessDenied (403)", Ct);

        await SaveRuleAsync(bucket, id: Guid.CreateVersion7(), endpoint: "https://other.example.org");

        var saved = (await Replication.FindRuleAsync(bucket.Id, Ct))!;
        Assert.Equal(first.Id, saved.Id);
        Assert.Equal("https://other.example.org", saved.Endpoint);
        Assert.Equal(["a", "b"], (await Replication.ListDueChangesAsync(Later, 100, Ct)).Select(static c => c.Key).Order());
    }

    [Fact]
    public async Task Pause_HoldsTheRulesChanges_UntilItEnds()
    {
        var bucket = await CreateBucketAsync();
        var rule = await SaveRuleAsync(bucket);
        await PutAsync(bucket, "a");

        await Replication.PauseRuleAsync(rule.Id, Later, "ServiceUnavailable (503)", Ct);

        Assert.Empty(await Replication.ListDueChangesAsync(Later.AddTicks(-1), 100, Ct));
        Assert.Single(await Replication.ListDueChangesAsync(Later, 100, Ct));
        Assert.Equal(Later, await Replication.NextDueAsync(Ct));
        var paused = (await Replication.FindRuleAsync(bucket.Id, Ct))!;
        Assert.Equal((1, "ServiceUnavailable (503)"), (paused.Failures, paused.LastError));
    }

    [Fact]
    public async Task Defer_DelaysOneKey_Fail_And_FailRule_ShowInTheBacklog_Retry_SendsAgain()
    {
        var bucket = await CreateBucketAsync();
        var rule = await SaveRuleAsync(bucket);
        var now = DateTime.UtcNow.AddMinutes(1); // changes carry the commit's time
        await PutAsync(bucket, "a");
        await PutAsync(bucket, "b");
        await PutAsync(bucket, "c");

        await Replication.DeferAsync(rule.Id, "a", long.MaxValue, Later, "blob gone", Ct);
        await Replication.FailAsync(rule.Id, "b", long.MaxValue, "EntityTooLarge (400)", Ct);
        Assert.Equal(["c"], (await Replication.ListDueChangesAsync(now, 100, Ct)).Select(static c => c.Key));

        var backlog = await Replication.GetBacklogAsync(rule.Id, 10, Ct);
        Assert.Equal((2L, 1L), (backlog.Pending, backlog.Failed));
        var failure = Assert.Single(backlog.Failures);
        Assert.Equal(("b", 1, "EntityTooLarge (400)"), (failure.Key, failure.Attempts, failure.LastError));

        await Replication.FailRuleAsync(rule.Id, "InvalidAccessKeyId (403)", Ct);
        Assert.Equal((0L, 3L), ((await Replication.GetBacklogAsync(rule.Id, 10, Ct)).Pending, (await Replication.GetBacklogAsync(rule.Id, 10, Ct)).Failed));

        Assert.Equal(3, await Replication.RetryAsync(rule.Id, now, Ct));
        Assert.Equal(["a", "b", "c"], (await Replication.ListDueChangesAsync(now, 100, Ct)).Select(static c => c.Key).Order());
        Assert.Null((await Replication.FindRuleAsync(bucket.Id, Ct))!.LastError);
    }

    [Fact]
    public async Task DeleteRule_RemovesItsQueue_AndARuleGoesWithItsBucket()
    {
        var bucket = await CreateBucketAsync();
        await SaveRuleAsync(bucket);
        await PutAsync(bucket, "a");

        Assert.True(await Replication.DeleteRuleAsync(bucket.Id, Ct));
        Assert.False(await Replication.DeleteRuleAsync(bucket.Id, Ct));
        Assert.Empty(await Replication.ListDueChangesAsync(Later, 100, Ct));

        var empty = await CreateBucketAsync("empty-bucket");
        await SaveRuleAsync(empty);
        Assert.Equal(BucketDeletion.Deleted, (await Store.DeleteBucketIfEmptyAsync(empty.Name, Ct)).Status);
        Assert.Empty(await Replication.ListRulesAsync(Ct));
        await PutAsync(bucket, "b"); // no rule left: nothing queued
        Assert.Empty(await Replication.ListDueChangesAsync(Later, 100, Ct));
    }

    [Fact]
    public async Task SaveRule_ForAMissingBucket_ReturnsFalse() =>
        Assert.False(await Replication.SaveRuleAsync(Rule(new Bucket(Guid.CreateVersion7(), "gone", Now)), false, Ct));

    [Fact]
    public async Task WaitForChanges_CompletesAfterACommitQueuedOne()
    {
        var bucket = await CreateBucketAsync();
        await SaveRuleAsync(bucket);
        var wait = Replication.WaitForChangesAsync(Ct).AsTask();
        while (wait.IsCompleted) // a signal left by an earlier write is allowed: take it
            wait = Replication.WaitForChangesAsync(Ct).AsTask();

        await PutAsync(bucket, "a");

        await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private async Task<Bucket> CreateBucketAsync(string name = "photos")
    {
        var bucket = new Bucket(Guid.CreateVersion7(), name, Now);
        Assert.True(await Store.TryCreateBucketAsync(bucket, Ct));
        return bucket;
    }

    private async Task<ReplicationRule> SaveRuleAsync(
        Bucket bucket, string prefix = "", bool replicateDeletes = true, bool queueExisting = false, Guid? id = null,
        string endpoint = "https://replica.example.org")
    {
        var rule = Rule(bucket, prefix, replicateDeletes, id, endpoint);
        Assert.True(await Replication.SaveRuleAsync(rule, queueExisting, Ct));
        return (await Replication.FindRuleAsync(bucket.Id, Ct))!;
    }

    private static ReplicationRule Rule(
        Bucket bucket, string prefix = "", bool replicateDeletes = true, Guid? id = null, string endpoint = "https://replica.example.org") =>
        new(id ?? Guid.CreateVersion7(), bucket.Id, endpoint, "replica", "us-east-1", "AKID", [1, 2, 3], prefix,
            replicateDeletes, Now, Now);

    private async Task PutAsync(Bucket bucket, string key) =>
        Assert.Equal(CommitStatus.Committed, (await Store.CommitObjectAsync(Object(bucket, key), CommitCondition.Always, Ct)).Status);

    private static StoredObject Object(Bucket bucket, string key) => new(
        bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(Blob()), Md5, StoredObject.DefaultContentType, null, null, null,
        [], [], Now);

    private static BlobLocator Blob() => new(Guid.CreateVersion7(), "node-1", 0, Md5);
}
