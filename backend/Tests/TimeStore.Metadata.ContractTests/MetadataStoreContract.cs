using TimeStore.Domain.Lifecycle;
using TimeStore.Domain.AccessKeys;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Domain.Stores;

namespace TimeStore.Metadata.ContractTests;

/// <summary>
/// The behaviour every <see cref="IMetadataStore"/> provider must have. A provider gets a subclass that creates a
/// fresh, empty store; all facts here run against it. Add a fact here (not in a provider's class) for new behaviour.
/// </summary>
public abstract class MetadataStoreContract : IAsyncLifetime
{
    protected IMetadataStore Store { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates an empty, initialized store.</summary>
    protected abstract ValueTask<IMetadataStore> CreateStoreAsync();

    /// <summary>Runs the provider's initializer again on the existing store (restart).</summary>
    protected abstract ValueTask ReinitializeAsync();

    public virtual async ValueTask InitializeAsync() => Store = await CreateStoreAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FindBucket_AfterCreate_ReturnsSameBucket()
    {
        var bucket = NewBucket("photos");
        Assert.True(await Store.TryCreateBucketAsync(bucket, Ct));

        var found = await Store.FindBucketAsync("photos", Ct);

        Assert.Equal(bucket, found);
    }

    [Fact]
    public async Task FindBucket_WhenMissing_ReturnsNull() =>
        Assert.Null(await Store.FindBucketAsync("missing", Ct));

    [Fact]
    public async Task TryCreateBucket_WithTakenName_ReturnsFalse()
    {
        Assert.True(await Store.TryCreateBucketAsync(NewBucket("photos"), Ct));

        Assert.False(await Store.TryCreateBucketAsync(NewBucket("photos"), Ct));
    }

    [Fact]
    public async Task TryCreateBucket_ConcurrentSameName_ExactlyOneWins()
    {
        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Store.TryCreateBucketAsync(NewBucket("photos"), Ct).AsTask());

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, created => created);
    }

    [Fact]
    public async Task Initialize_RunTwice_KeepsData()
    {
        await Store.TryCreateBucketAsync(NewBucket("photos"), Ct);

        await ReinitializeAsync();

        Assert.NotNull(await Store.FindBucketAsync("photos", Ct));
    }

    [Fact]
    public async Task ListBuckets_ReturnsBinaryNameOrder()
    {
        foreach (var name in new[] { "b-bucket", "a.bucket", "a-bucket", "a0bucket" })
            await Store.TryCreateBucketAsync(NewBucket(name), Ct);

        var names = (await Store.ListBucketsAsync(Ct)).Select(b => b.Name);

        Assert.Equal(new[] { "a-bucket", "a.bucket", "a0bucket", "b-bucket" }, names); // '-' < '.' < '0' in ASCII
    }

    [Fact]
    public async Task DeleteBucket_RemovesItOnce()
    {
        await Store.TryCreateBucketAsync(NewBucket("photos"), Ct);

        Assert.Equal(BucketDeletion.Deleted, (await Store.DeleteBucketIfEmptyAsync("photos", Ct)).Status);
        Assert.Equal(BucketDeletion.NotFound, (await Store.DeleteBucketIfEmptyAsync("photos", Ct)).Status);
        Assert.Null(await Store.FindBucketAsync("photos", Ct));
    }

    [Fact]
    public async Task DeleteBucket_WithObjects_IsNotEmpty_UntilTheyAreGone()
    {
        var bucket = await CreateBucketAsync();
        await Store.CommitObjectAsync(NewObject(bucket, "a.txt"), CommitCondition.Always, Ct);

        Assert.Equal(BucketDeletion.NotEmpty, (await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct)).Status);

        await Store.DeleteObjectAsync(bucket.Id, "a.txt", Ct);
        Assert.Equal(BucketDeletion.Deleted, (await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct)).Status);
    }

    // --- Blob references -----------------------------------------------------------------------------------------

    [Fact]
    public async Task FindUnreferencedBlobs_ReturnsOnlyIdsNoRowUses_AcrossBatches()
    {
        var bucket = await CreateBucketAsync();
        var used = NewObject(bucket, "used");
        await Store.CommitObjectAsync(used, CommitCondition.Always, Ct);
        var replaced = NewObject(bucket, "replaced");
        await Store.CommitObjectAsync(replaced, CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(NewObject(bucket, "replaced"), CommitCondition.Always, Ct); // replaced's blob now unused
        var orphans = Enumerable.Range(0, 1200).Select(_ => Guid.CreateVersion7()).ToList(); // more than one batch

        var candidates = orphans.Concat([BlobOf(used).BlobId, BlobOf(replaced).BlobId]).ToList();
        var unreferenced = await Store.FindUnreferencedBlobsAsync(candidates, Ct);

        // The replaced blob is no object's any more but waits for its collector (released), so it is not an orphan.
        Assert.Equal(orphans.Order(), unreferenced.Order());
        Assert.Equal([BlobOf(replaced).BlobId], await ReleasedIdsAsync());
        Assert.Empty(await Store.FindUnreferencedBlobsAsync([], Ct));
    }

    // --- Multipart uploads -----------------------------------------------------------------------------------------

    private static MultipartUpload NewUpload(Bucket bucket, string key) => new(
        Guid.CreateVersion7(), bucket.Id, key, "video/mp4", "CRC32", ChecksumTypes.Composite,
        [new("cache-control", "no-cache")], [new("owner", "felix")], Timestamp);

    private static UploadPart NewPart(MultipartUpload upload, int number, long size = 10) =>
        new(upload.UploadId, number, NewBlob(size), [1, 2, 3, 4], Timestamp);

    private async Task<MultipartUpload> CreateUploadAsync(Bucket bucket, string key = "movie.mp4")
    {
        var upload = NewUpload(bucket, key);
        Assert.True(await Store.TryCreateUploadAsync(upload, Ct));
        return upload;
    }

    /// <summary>The object CompleteMultipartUpload would build from <paramref name="parts"/>.</summary>
    private static StoredObject Manifest(MultipartUpload upload, params UploadPart[] parts)
    {
        var offset = 0L;
        var manifest = new List<ObjectPart>();
        foreach (var part in parts)
        {
            manifest.Add(new ObjectPart(part.PartNumber, offset, part.Blob, part.Checksum));
            offset += part.Blob.Size;
        }

        return new StoredObject(
            upload.BucketId, upload.Key, Guid.CreateVersion7(), new MultipartBlobs(upload.UploadId, manifest),
            $"{"0":x32}-{parts.Length}", upload.ContentType, upload.ChecksumAlgorithm, [9, 9, 9, 9], upload.ChecksumType,
            upload.Headers, upload.UserMetadata, Timestamp);
    }

    [Fact]
    public async Task Upload_CreateFind_RoundTrips_AndIsScopedToBucketAndKey()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);

        var found = (await Store.FindUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct))!;

        Assert.Equal(upload.ContentType, found.ContentType);
        Assert.Equal(upload.ChecksumAlgorithm, found.ChecksumAlgorithm);
        Assert.Equal(upload.ChecksumType, found.ChecksumType);
        Assert.Equal(upload.Headers, found.Headers);
        Assert.Equal(upload.UserMetadata, found.UserMetadata);
        Assert.Equal(upload.CreatedAt, found.CreatedAt);
        Assert.Null(await Store.FindUploadAsync(bucket.Id, "other-key", upload.UploadId, Ct));
        Assert.False(await Store.TryCreateUploadAsync(NewUpload(NewBucket("never-created"), "a"), Ct));
    }

    [Fact]
    public async Task Parts_ReplaceReturnsOldBlob_ListPages_AbortReturnsEveryBlob()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var first = NewPart(upload, 1);
        Assert.Null((await Store.CommitPartAsync(first, Ct)).Replaced);
        var again = NewPart(upload, 1);
        var commit = await Store.CommitPartAsync(again, Ct);
        await Store.CommitPartAsync(NewPart(upload, 3), Ct);
        await Store.CommitPartAsync(NewPart(upload, 2), Ct);

        Assert.Equal(first.Blob, commit.Replaced);
        var page = await Store.ListPartsAsync(upload.UploadId, afterPartNumber: 0, maxParts: 2, Ct);
        Assert.Equal([1, 2], page.Parts.Select(p => p.PartNumber));
        Assert.True(page.IsTruncated);
        Assert.Equal(again.Blob, page.Parts[0].Blob);
        Assert.Equal(again.Checksum, page.Parts[0].Checksum);
        var rest = await Store.ListPartsAsync(upload.UploadId, afterPartNumber: 2, maxParts: 2, Ct);
        Assert.Equal([3], rest.Parts.Select(p => p.PartNumber));
        Assert.False(rest.IsTruncated);

        var aborted = await Store.AbortUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct);
        Assert.Equal(3, aborted!.Count);
        Assert.Null(await Store.AbortUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct));
        Assert.Equal(PartCommitStatus.UploadMissing, (await Store.CommitPartAsync(NewPart(upload, 4), Ct)).Status);
    }

    [Fact]
    public async Task Complete_WritesTheManifest_RemovesTheUpload_ReturnsUnlistedParts()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var p1 = NewPart(upload, 1, size: 100);
        var p2 = NewPart(upload, 2, size: 50);
        var unlisted = NewPart(upload, 5);
        foreach (var part in new[] { p1, p2, unlisted })
            await Store.CommitPartAsync(part, Ct);
        var stored = Manifest(upload, p1, p2);

        var completion = await Store.CompleteUploadAsync(stored, CommitCondition.Always, Ct);

        Assert.Equal(CompletionStatus.Completed, completion.Status);
        Assert.Null(completion.Replaced);
        Assert.Equal([unlisted.Blob], completion.Discarded);
        Assert.Null(await Store.FindUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct));

        var found = (await Store.FindObjectAsync(bucket.Id, upload.Key, Ct))!;
        var data = Assert.IsType<MultipartBlobs>(found.Data);
        Assert.Equal(upload.UploadId, data.UploadId);
        Assert.Equal(((MultipartBlobs)stored.Data).Parts, data.Parts, PartEquals);
        Assert.Equal(150, found.Size);
        Assert.Equal(stored.ETag, found.ETag);
        Assert.Equal(ChecksumTypes.Composite, found.ChecksumType);
        Assert.Equal(stored.Checksum, found.Checksum);
        Assert.Equal(150, Assert.Single((await Store.ListObjectsAsync(bucket.Id, "", null, null, 10, Ct)).Objects).Size);
        Assert.Equal([unlisted.Blob.BlobId], await ReleasedIdsAsync()); // only the collector holds it now
    }

    [Fact]
    public async Task Complete_OverwriteAndDelete_HandBackEveryPartBlob()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket, "a.bin");
        var parts = new[] { NewPart(upload, 1), NewPart(upload, 2) };
        foreach (var part in parts)
            await Store.CommitPartAsync(part, Ct);
        await Store.CompleteUploadAsync(Manifest(upload, parts), CommitCondition.Always, Ct);

        // A single PUT over a multipart object hands back its manifest and drops the part rows.
        var single = NewObject(bucket, "a.bin");
        var commit = await Store.CommitObjectAsync(single, CommitCondition.Always, Ct);
        Assert.Equal(parts.Select(p => p.Blob), Assert.IsType<MultipartBlobs>(commit.Replaced).Blobs);
        Assert.Equal(parts.Select(p => p.Blob.BlobId).Order(), await ReleasedIdsAsync());

        // A multipart upload over a single object hands back the single blob; deleting it hands back every part.
        var second = await CreateUploadAsync(bucket, "a.bin");
        var secondParts = new[] { NewPart(second, 1), NewPart(second, 2) };
        foreach (var part in secondParts)
            await Store.CommitPartAsync(part, Ct);
        var completion = await Store.CompleteUploadAsync(Manifest(second, secondParts), CommitCondition.Always, Ct);
        Assert.Equal(single.Data, completion.Replaced);
        Assert.Equal(secondParts.Select(p => p.Blob), Assert.IsType<MultipartBlobs>(await Store.DeleteObjectAsync(bucket.Id, "a.bin", Ct)).Blobs);
    }

    [Fact]
    public async Task Complete_AfterAPartChanged_WritesNothing()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var part = NewPart(upload, 1);
        await Store.CommitPartAsync(part, Ct);
        var stored = Manifest(upload, part);
        await Store.CommitPartAsync(NewPart(upload, 1), Ct); // re-uploaded after the caller validated

        var completion = await Store.CompleteUploadAsync(stored, CommitCondition.Always, Ct);

        Assert.Equal(CompletionStatus.PartsChanged, completion.Status);
        Assert.Null(await Store.FindObjectAsync(bucket.Id, upload.Key, Ct));
        Assert.NotNull(await Store.FindUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct));
    }

    [Fact]
    public async Task Complete_IfAbsent_OverAnExistingObject_WritesNothing()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket, "a.txt");
        var part = NewPart(upload, 1);
        await Store.CommitPartAsync(part, Ct);
        await Store.CommitObjectAsync(NewObject(bucket, "a.txt"), CommitCondition.Always, Ct);

        var completion = await Store.CompleteUploadAsync(Manifest(upload, part), CommitCondition.IfAbsent, Ct);

        Assert.Equal(CompletionStatus.ConditionFailed, completion.Status);
        Assert.IsType<SingleBlob>((await Store.FindObjectAsync(bucket.Id, "a.txt", Ct))!.Data);
    }

    [Fact]
    public async Task Complete_Concurrent_ExactlyOneWins()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var part = NewPart(upload, 1);
        await Store.CommitPartAsync(part, Ct);

        var completions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Store.CompleteUploadAsync(Manifest(upload, part), CommitCondition.Always, Ct).AsTask()));

        Assert.Single(completions, c => c.Status == CompletionStatus.Completed);
        Assert.Equal(7, completions.Count(c => c.Status == CompletionStatus.UploadMissing));
    }

    [Fact]
    public async Task UploadsInProgress_AreAbortedWithTheirBucket_AndHandBackTheirParts()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var parts = new[] { NewPart(upload, 1), NewPart(upload, 2) };
        foreach (var part in parts)
            await Store.CommitPartAsync(part, Ct);

        var deletion = await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct);

        Assert.Equal(BucketDeletion.Deleted, deletion.Status);
        Assert.Equal(parts.Select(p => p.Blob).OrderBy(b => b.BlobId), deletion.FreedBlobs.OrderBy(b => b.BlobId));
        Assert.Null(await Store.FindUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct));
        Assert.Equal(parts.Select(p => p.Blob.BlobId).Order(), await ReleasedIdsAsync());
    }

    [Fact]
    public async Task AnObject_StillKeepsABucketWithUploadsFromBeingDeleted()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        await Store.CommitObjectAsync(NewObject(bucket, "kept"), CommitCondition.Always, Ct);

        Assert.Equal(BucketDeletion.NotEmpty, (await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct)).Status);
        Assert.NotNull(await Store.FindUploadAsync(bucket.Id, upload.Key, upload.UploadId, Ct)); // nothing was aborted
    }

    [Fact]
    public async Task ListUploads_OrdersByKeyThenInitiation_HonoursMarkers()
    {
        var bucket = await CreateBucketAsync();
        var b1 = await CreateUploadAsync(bucket, "b");
        var a = await CreateUploadAsync(bucket, "a");
        var b2 = await CreateUploadAsync(bucket, "b");
        var c = await CreateUploadAsync(bucket, "c");
        // Same key: upload id order (UUIDv7 = initiation time; two ids of one millisecond have no defined order).
        if (string.CompareOrdinal(b1.UploadId.ToString("N"), b2.UploadId.ToString("N")) > 0)
            (b1, b2) = (b2, b1);

        var all = await Store.ListUploadsAsync(bucket.Id, "", null, null, null, 10, Ct);
        Assert.Equal([a.UploadId, b1.UploadId, b2.UploadId, c.UploadId], all.Uploads.Select(u => u.UploadId));
        Assert.False(all.IsTruncated);

        var first = await Store.ListUploadsAsync(bucket.Id, "", null, null, null, 2, Ct);
        Assert.True(first.IsTruncated);
        Assert.Equal(("b", b1.UploadId), (first.NextKeyMarker, first.NextUploadIdMarker));
        var next = await Store.ListUploadsAsync(bucket.Id, "", null, first.NextKeyMarker, first.NextUploadIdMarker, 2, Ct);
        Assert.Equal([b2.UploadId, c.UploadId], next.Uploads.Select(u => u.UploadId));

        // A key marker alone skips every upload of that key.
        var afterB = await Store.ListUploadsAsync(bucket.Id, "", null, "b", null, 10, Ct);
        Assert.Equal([c.UploadId], afterB.Uploads.Select(u => u.UploadId));
    }

    [Fact]
    public async Task ListUploads_WithDelimiter_RollsUpCommonPrefixes()
    {
        var bucket = await CreateBucketAsync();
        foreach (var key in new[] { "photos/1.jpg", "photos/2.jpg", "photos/raw/3.dng", "readme.txt", "videos/a.mp4" })
            await CreateUploadAsync(bucket, key);

        var page = await Store.ListUploadsAsync(bucket.Id, "", "/", null, null, 10, Ct);
        Assert.Equal(["readme.txt"], page.Uploads.Select(u => u.Key));
        Assert.Equal(["photos/", "videos/"], page.CommonPrefixes);

        var inner = await Store.ListUploadsAsync(bucket.Id, "photos/", "/", null, null, 10, Ct);
        Assert.Equal(["photos/1.jpg", "photos/2.jpg"], inner.Uploads.Select(u => u.Key));
        Assert.Equal(["photos/raw/"], inner.CommonPrefixes);

        var resumed = await Store.ListUploadsAsync(bucket.Id, "", "/", "photos/", null, 10, Ct);
        Assert.Equal(["readme.txt"], resumed.Uploads.Select(u => u.Key));
        Assert.Equal(["videos/"], resumed.CommonPrefixes);
    }

    [Fact]
    public async Task ListUploadsCreatedBefore_ReturnsOldUploadsOldestFirst()
    {
        var bucket = await CreateBucketAsync();
        var oldest = NewUpload(bucket, "a") with { CreatedAt = Timestamp.AddDays(-9) };
        var old = NewUpload(bucket, "b") with { CreatedAt = Timestamp.AddDays(-8) };
        var recent = NewUpload(bucket, "c") with { CreatedAt = Timestamp };
        foreach (var upload in new[] { recent, old, oldest })
            Assert.True(await Store.TryCreateUploadAsync(upload, Ct));

        var found = await Store.ListUploadsCreatedBeforeAsync(Timestamp.AddDays(-7), 10, Ct);
        var limited = await Store.ListUploadsCreatedBeforeAsync(Timestamp.AddDays(-7), 1, Ct);

        Assert.Equal(
            [new UploadRef(bucket.Id, "a", oldest.UploadId), new UploadRef(bucket.Id, "b", old.UploadId)], found);
        Assert.Equal([oldest.UploadId], limited.Select(u => u.UploadId));
    }

    private static readonly Func<ObjectPart, ObjectPart, bool> PartEquals = (x, y) =>
        x.PartNumber == y.PartNumber && x.Offset == y.Offset && x.Blob == y.Blob
        && (x.Checksum ?? []).AsSpan().SequenceEqual(y.Checksum ?? []);

    // --- Access keys -----------------------------------------------------------------------------------------------

    private static AccessKey NewAccessKey(string id) =>
        new(id, [1, 2, 3, 4, 5], "ci pipeline", AccessKeyRoles.None, Timestamp, DisabledAt: null);

    [Fact]
    public async Task AccessKey_CreateFindListDisableEnableDelete()
    {
        var key = NewAccessKey("TSAAAAAAAAAAAAAAAAAA");
        Assert.True(await Store.TryCreateAccessKeyAsync(key, Ct));
        Assert.False(await Store.TryCreateAccessKeyAsync(key, Ct)); // id taken

        var found = (await Store.FindAccessKeyAsync(key.AccessKeyId, Ct))!;
        Assert.Equal(key.SecretCiphertext, found.SecretCiphertext);
        Assert.Equal(key.Description, found.Description);
        Assert.Equal(key.CreatedAt, found.CreatedAt);
        Assert.True(found.IsActive);
        Assert.Equal(new[] { key.AccessKeyId }, (await Store.ListAccessKeysAsync(Ct)).Select(k => k.AccessKeyId));

        Assert.True(await Store.SetAccessKeyDisabledAsync(key.AccessKeyId, Timestamp, Ct));
        Assert.Equal(Timestamp, (await Store.FindAccessKeyAsync(key.AccessKeyId, Ct))!.DisabledAt);
        Assert.True(await Store.SetAccessKeyDisabledAsync(key.AccessKeyId, null, Ct));
        Assert.True((await Store.FindAccessKeyAsync(key.AccessKeyId, Ct))!.IsActive);

        Assert.True(await Store.DeleteAccessKeyAsync(key.AccessKeyId, Ct));
        Assert.False(await Store.DeleteAccessKeyAsync(key.AccessKeyId, Ct));
        Assert.Null(await Store.FindAccessKeyAsync(key.AccessKeyId, Ct));
    }

    [Fact]
    public async Task AccessKey_Role_RoundTripsAndChanges()
    {
        var key = NewAccessKey("TSROLEROLEROLEROLE00") with { Role = AccessKeyRoles.Viewer };
        await Store.TryCreateAccessKeyAsync(key, Ct);

        Assert.Equal(AccessKeyRoles.Viewer, (await Store.FindAccessKeyAsync(key.AccessKeyId, Ct))!.Role);
        Assert.True(await Store.SetAccessKeyRoleAsync(key.AccessKeyId, AccessKeyRoles.Admin, Ct));
        Assert.Equal(AccessKeyRoles.Admin, (await Store.FindAccessKeyAsync(key.AccessKeyId, Ct))!.Role);
        Assert.False(await Store.SetAccessKeyRoleAsync("TSMISSING0000000000X", AccessKeyRoles.Admin, Ct));
    }

    [Fact]
    public async Task AccessKey_S3Scope_DefaultsToFull_RoundTripsAndChanges()
    {
        var plain = NewAccessKey("TSSCOPEFULL000000000");
        var limited = NewAccessKey("TSSCOPEPOLICY0000000") with { S3Scope = AccessKeyScopes.Policy };
        await Store.TryCreateAccessKeyAsync(plain, Ct);
        await Store.TryCreateAccessKeyAsync(limited, Ct);

        Assert.Equal(AccessKeyScopes.Full, (await Store.FindAccessKeyAsync(plain.AccessKeyId, Ct))!.S3Scope);
        Assert.Equal(AccessKeyScopes.Policy, (await Store.FindAccessKeyAsync(limited.AccessKeyId, Ct))!.S3Scope);
        Assert.True(await Store.SetAccessKeyScopeAsync(plain.AccessKeyId, AccessKeyScopes.Policy, Ct));
        Assert.Equal(AccessKeyScopes.Policy, (await Store.FindAccessKeyAsync(plain.AccessKeyId, Ct))!.S3Scope);
        Assert.False(await Store.SetAccessKeyScopeAsync("TSMISSING0000000000X", AccessKeyScopes.Full, Ct));
    }

    // --- Bucket policies (step 6d) ---------------------------------------------------------------------------------

    [Fact]
    public async Task BucketPolicy_RoundTripsVerbatim_Replaces_AndGoesWithTheBucket()
    {
        var bucket = await CreateBucketAsync();
        Assert.Null(await Store.GetBucketPolicyAsync(bucket.Id, Ct));

        const string first = "{ \"Version\": \"2012-10-17\",\n  \"Statement\": [] }"; // stored as sent, whitespace too
        Assert.True(await Store.SetBucketPolicyAsync(bucket.Id, first, Timestamp, Ct));
        Assert.Equal(first, await Store.GetBucketPolicyAsync(bucket.Id, Ct));
        Assert.True(await Store.SetBucketPolicyAsync(bucket.Id, "{}", Timestamp, Ct));
        Assert.Equal("{}", await Store.GetBucketPolicyAsync(bucket.Id, Ct));

        await Store.DeleteBucketPolicyAsync(bucket.Id, Ct);
        Assert.Null(await Store.GetBucketPolicyAsync(bucket.Id, Ct));
        await Store.DeleteBucketPolicyAsync(bucket.Id, Ct); // idempotent

        await Store.SetBucketPolicyAsync(bucket.Id, "{}", Timestamp, Ct);
        await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct);
        Assert.Null(await Store.GetBucketPolicyAsync(bucket.Id, Ct));
        Assert.False(await Store.SetBucketPolicyAsync(bucket.Id, "{}", Timestamp, Ct));
    }

    [Fact]
    public async Task BucketPolicy_Replace_IsACompareAndSwap()
    {
        var bucket = await CreateBucketAsync();
        Assert.Equal(PolicyReplace.Changed, await Store.ReplaceBucketPolicyAsync(bucket.Id, "{}", "{\"a\":1}", Timestamp, Ct));
        Assert.Equal(PolicyReplace.Replaced, await Store.ReplaceBucketPolicyAsync(bucket.Id, null, "{\"a\":1}", Timestamp, Ct));
        Assert.Equal(PolicyReplace.Changed, await Store.ReplaceBucketPolicyAsync(bucket.Id, null, "{\"b\":2}", Timestamp, Ct));
        Assert.Equal(PolicyReplace.Replaced, await Store.ReplaceBucketPolicyAsync(bucket.Id, "{\"a\":1}", "{\"b\":2}", Timestamp, Ct));
        Assert.Equal("{\"b\":2}", await Store.GetBucketPolicyAsync(bucket.Id, Ct));
        Assert.Equal(PolicyReplace.Replaced, await Store.ReplaceBucketPolicyAsync(bucket.Id, "{\"b\":2}", null, Timestamp, Ct));
        Assert.Null(await Store.GetBucketPolicyAsync(bucket.Id, Ct));
        Assert.Equal(PolicyReplace.BucketMissing, await Store.ReplaceBucketPolicyAsync(Guid.CreateVersion7(), null, "{}", Timestamp, Ct));
    }

    [Fact]
    public async Task Sessions_CreateFindDelete_ByKey_AndExpired()
    {
        AdminSession Session(byte fill, string key, DateTime expires) =>
            new(Enumerable.Repeat(fill, 32).ToArray(), key, Timestamp, expires);
        var current = Session(1, "TSKEYA", Timestamp.AddHours(24));
        var sameKey = Session(2, "TSKEYA", Timestamp.AddHours(24));
        var expired = Session(3, "TSKEYB", Timestamp.AddMinutes(-1));
        foreach (var session in new[] { current, sameKey, expired })
            await Store.CreateSessionAsync(session, Ct);

        var found = (await Store.FindSessionAsync(current.TokenHash, Ct))!;
        Assert.Equal((current.AccessKeyId, current.CreatedAt, current.ExpiresAt), (found.AccessKeyId, found.CreatedAt, found.ExpiresAt));
        Assert.Null(await Store.FindSessionAsync(new byte[32], Ct));

        Assert.Equal(1, await Store.DeleteExpiredSessionsAsync(Timestamp, Ct));
        Assert.Null(await Store.FindSessionAsync(expired.TokenHash, Ct));
        Assert.Equal(2, await Store.DeleteSessionsOfAsync("TSKEYA", Ct));
        Assert.Null(await Store.FindSessionAsync(sameKey.TokenHash, Ct));

        await Store.CreateSessionAsync(current, Ct);
        await Store.DeleteSessionAsync(current.TokenHash, Ct);
        Assert.Null(await Store.FindSessionAsync(current.TokenHash, Ct));
    }

    [Fact]
    public async Task AccessKey_MissingKey_CannotBeDisabled() =>
        Assert.False(await Store.SetAccessKeyDisabledAsync("TSMISSING0000000000X", Timestamp, Ct));

    // --- Objects -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CommitObject_ThenFind_RoundTripsEveryField()
    {
        var bucket = await CreateBucketAsync();
        var stored = NewObject(bucket, "photos/ảnh 1.jpg") with
        {
            ContentType = "image/jpeg",
            ChecksumAlgorithm = "CRC32C",
            Checksum = [1, 2, 3, 4],
            Headers = [new("cache-control", "max-age=60"), new("content-disposition", "attachment; filename=\"a:b.jpg\"")],
            UserMetadata = [new("owner", "felix"), new("note", "a:b c")],
        };

        var commit = await Store.CommitObjectAsync(stored, CommitCondition.Always, Ct);
        var found = await Store.FindObjectAsync(bucket.Id, stored.Key, Ct);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Null(commit.Replaced);
        Assert.NotNull(found);
        Assert.Equal(stored.Key, found.Key);
        Assert.Equal(stored.VersionId, found.VersionId);
        Assert.Equal(stored.Data, found.Data);
        Assert.Null(found.ChecksumType);
        Assert.Equal(stored.ETag, found.ETag);
        Assert.Equal(stored.ContentType, found.ContentType);
        Assert.Equal(stored.ChecksumAlgorithm, found.ChecksumAlgorithm);
        Assert.Equal(stored.Checksum, found.Checksum);
        Assert.Equal(stored.Headers, found.Headers);
        Assert.Equal(stored.UserMetadata, found.UserMetadata);
        Assert.Equal(stored.CreatedAt, found.CreatedAt);
    }

    [Fact]
    public async Task CommitObject_WithoutOptionalFields_RoundTrips()
    {
        var bucket = await CreateBucketAsync();
        var stored = NewObject(bucket, "plain");

        await Store.CommitObjectAsync(stored, CommitCondition.Always, Ct);
        var found = (await Store.FindObjectAsync(bucket.Id, "plain", Ct))!;

        Assert.Null(found.ChecksumAlgorithm);
        Assert.Null(found.Checksum);
        Assert.Empty(found.Headers);
        Assert.Empty(found.UserMetadata);
    }

    [Fact]
    public async Task CommitObject_Overwrite_ReturnsReplacedBlob()
    {
        var bucket = await CreateBucketAsync();
        var first = NewObject(bucket, "a.txt");
        var second = NewObject(bucket, "a.txt");
        await Store.CommitObjectAsync(first, CommitCondition.Always, Ct);

        var commit = await Store.CommitObjectAsync(second, CommitCondition.Always, Ct);

        Assert.Equal(first.Data, commit.Replaced);
        Assert.Equal(second.Data, (await Store.FindObjectAsync(bucket.Id, "a.txt", Ct))!.Data);
    }

    [Fact]
    public async Task CommitObject_ToMissingBucket_WritesNothing()
    {
        var bucket = NewBucket("never-created");

        var commit = await Store.CommitObjectAsync(NewObject(bucket, "a.txt"), CommitCondition.Always, Ct);

        Assert.Equal(CommitStatus.BucketMissing, commit.Status);
        Assert.Null(await Store.FindObjectAsync(bucket.Id, "a.txt", Ct));
    }

    [Fact]
    public async Task CommitObject_ConcurrentWriters_NoBlobIsLost()
    {
        var bucket = await CreateBucketAsync();
        var writes = Enumerable.Range(0, 16).Select(_ => NewObject(bucket, "race.txt")).ToArray();

        var commits = await Task.WhenAll(writes.Select(w => Store.CommitObjectAsync(w, CommitCondition.Always, Ct).AsTask()));
        var final = (await Store.FindObjectAsync(bucket.Id, "race.txt", Ct))!;

        // Every blob is either the final one or was handed back exactly once for deletion.
        var replaced = commits.Select(c => c.Replaced).OfType<SingleBlob>().ToList();
        Assert.Equal(writes.Length - 1, replaced.Count);
        Assert.Equal(
            writes.Select(w => BlobOf(w).BlobId).Order(),
            replaced.Select(b => b.Blob.BlobId).Append(BlobOf(final).BlobId).Order());
    }

    [Fact]
    public async Task CommitObject_IfAbsent_OnlyTheFirstWins()
    {
        var bucket = await CreateBucketAsync();
        var writes = Enumerable.Range(0, 16).Select(_ => NewObject(bucket, "once.txt")).ToArray();

        var commits = await Task.WhenAll(writes.Select(w => Store.CommitObjectAsync(w, CommitCondition.IfAbsent, Ct).AsTask()));

        Assert.Single(commits, c => c.Status == CommitStatus.Committed);
        Assert.Equal(writes.Length - 1, commits.Count(c => c.Status == CommitStatus.ConditionFailed));
        Assert.All(commits, c => Assert.Null(c.Replaced));
    }

    [Fact]
    public async Task DeleteObject_ReturnsBlobOnce()
    {
        var bucket = await CreateBucketAsync();
        var stored = NewObject(bucket, "a.txt");
        await Store.CommitObjectAsync(stored, CommitCondition.Always, Ct);

        Assert.Equal(stored.Data, await Store.DeleteObjectAsync(bucket.Id, "a.txt", Ct));
        Assert.Null(await Store.DeleteObjectAsync(bucket.Id, "a.txt", Ct));
        Assert.Null(await Store.FindObjectAsync(bucket.Id, "a.txt", Ct));
    }

    [Fact]
    public async Task DeleteObjects_DeletesExistingKeys_SkipsMissing_AndDuplicatesOnce()
    {
        var bucket = await CreateBucketAsync();
        var a = NewObject(bucket, "a");
        var b = NewObject(bucket, "b");
        await Store.CommitObjectAsync(a, CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(b, CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(NewObject(bucket, "keep"), CommitCondition.Always, Ct);

        var deleted = await Store.DeleteObjectsAsync(bucket.Id, ["a", "missing", "b", "a"], Ct);

        Assert.Equal(new[] { BlobOf(a), BlobOf(b) }, deleted);
        Assert.Null(await Store.FindObjectAsync(bucket.Id, "a", Ct));
        Assert.NotNull(await Store.FindObjectAsync(bucket.Id, "keep", Ct));
    }

    // --- Listing -------------------------------------------------------------------------------------------------

    private async Task<Bucket> BucketWithKeysAsync(params string[] keys)
    {
        var bucket = await CreateBucketAsync();
        foreach (var key in keys)
            await Store.CommitObjectAsync(NewObject(bucket, key), CommitCondition.Always, Ct);
        return bucket;
    }

    private async Task<(string[] Keys, string[] Prefixes, ObjectPage Page)> List(
        Bucket bucket, string prefix = "", string? delimiter = null, string? after = null, int max = 1000)
    {
        var page = await Store.ListObjectsAsync(bucket.Id, prefix, delimiter, after, max, Ct);
        return (page.Objects.Select(o => o.Key).ToArray(), page.CommonPrefixes.ToArray(), page);
    }

    [Fact]
    public async Task List_ReturnsUtf8BinaryOrder()
    {
        var bucket = await BucketWithKeysAsync("ä", "z", "a", "A", "a/b", "a-b", "", "😀");

        var (keys, _, page) = await List(bucket);

        // UTF-8 byte order: 'A' < 'a' < "a-b" < "a/b" < 'z' < 'ä' (C3 A4) < U+E000 (EE..) < 😀 (F0..).
        Assert.Equal(new[] { "A", "a", "a-b", "a/b", "z", "ä", "", "😀" }, keys);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task List_PrefixAndStartAfter_Filter()
    {
        var bucket = await BucketWithKeysAsync("logs/1", "logs/2", "logs/3", "logsx", "data/1");

        Assert.Equal(new[] { "logs/1", "logs/2", "logs/3" }, (await List(bucket, prefix: "logs/")).Keys);
        Assert.Equal(new[] { "logs/3" }, (await List(bucket, prefix: "logs/", after: "logs/2")).Keys);
    }

    [Fact]
    public async Task List_Paging_ResumesFromLastEntry()
    {
        var bucket = await BucketWithKeysAsync("a", "b", "c", "d", "e");

        var first = await List(bucket, max: 2);
        var second = await List(bucket, after: first.Page.LastEntry, max: 2);
        var third = await List(bucket, after: second.Page.LastEntry, max: 2);

        Assert.Equal(new[] { "a", "b" }, first.Keys);
        Assert.True(first.Page.IsTruncated);
        Assert.Equal(new[] { "c", "d" }, second.Keys);
        Assert.True(second.Page.IsTruncated);
        Assert.Equal(new[] { "e" }, third.Keys);
        Assert.False(third.Page.IsTruncated);
    }

    [Fact]
    public async Task List_ExactlyFullPage_IsNotTruncated()
    {
        var bucket = await BucketWithKeysAsync("a", "b");

        Assert.False((await List(bucket, max: 2)).Page.IsTruncated);
    }

    [Fact]
    public async Task List_MaxZero_IsEmptyAndNotTruncated()
    {
        var bucket = await BucketWithKeysAsync("a");

        var (keys, prefixes, page) = await List(bucket, max: 0);

        Assert.Empty(keys);
        Assert.Empty(prefixes);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task List_Delimiter_RollsUpCommonPrefixes()
    {
        var bucket = await BucketWithKeysAsync("a.txt", "photos/2025/x.jpg", "photos/2026/y.jpg", "photos/cat.jpg", "z.txt");

        var root = await List(bucket, delimiter: "/");
        var photos = await List(bucket, prefix: "photos/", delimiter: "/");

        Assert.Equal(new[] { "a.txt", "z.txt" }, root.Keys);
        Assert.Equal(new[] { "photos/" }, root.Prefixes);
        Assert.Equal(new[] { "photos/cat.jpg" }, photos.Keys);
        Assert.Equal(new[] { "photos/2025/", "photos/2026/" }, photos.Prefixes);
    }

    [Fact]
    public async Task List_CommonPrefixesCountAgainstMax_AndPagingSkipsThem()
    {
        var bucket = await BucketWithKeysAsync("a/1", "a/2", "b/1", "c.txt", "d/1");

        var first = await List(bucket, delimiter: "/", max: 2);
        var second = await List(bucket, delimiter: "/", after: first.Page.LastEntry, max: 2);

        Assert.Equal(new[] { "a/", "b/" }, first.Prefixes);
        Assert.True(first.Page.IsTruncated);
        Assert.Equal("b/", first.Page.LastEntry);
        Assert.Equal(new[] { "c.txt" }, second.Keys);
        Assert.Equal(new[] { "d/" }, second.Prefixes);
        Assert.False(second.Page.IsTruncated);
    }

    [Fact]
    public async Task List_MultiCharacterDelimiter()
    {
        var bucket = await BucketWithKeysAsync("a--b--c", "a--d", "a-e");

        var (keys, prefixes, _) = await List(bucket, delimiter: "--");

        Assert.Equal(new[] { "a-e" }, keys);
        Assert.Equal(new[] { "a--" }, prefixes);
    }

    [Fact]
    public async Task List_HugeCommonPrefix_StaysCorrect()
    {
        var bucket = await CreateBucketAsync();
        var keys = Enumerable.Range(0, 2_000).Select(i => $"logs/{i:D5}").Append("a.txt").Append("z.txt");
        foreach (var key in keys)
            await Store.CommitObjectAsync(NewObject(bucket, key), CommitCondition.Always, Ct);

        var (listed, prefixes, page) = await List(bucket, delimiter: "/");

        Assert.Equal(new[] { "a.txt", "z.txt" }, listed);
        Assert.Equal(new[] { "logs/" }, prefixes);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task List_OnlySeesItsOwnBucket()
    {
        var mine = await BucketWithKeysAsync("a");
        await BucketWithKeysAsync("b");

        Assert.Equal(new[] { "a" }, (await List(mine)).Keys);
    }

    // --- Versioning (step 6a) ------------------------------------------------------------------------------------

    private async Task<Bucket> VersionedBucketAsync(BucketVersioning versioning = BucketVersioning.Enabled)
    {
        var bucket = await CreateBucketAsync();
        Assert.True(await Store.SetBucketVersioningAsync(bucket.Name, versioning, Ct));
        return bucket;
    }

    private async Task<ObjectVersion> PutAsync(Bucket bucket, string key, long size = 42)
    {
        var commit = await Store.CommitObjectAsync(
            NewObject(bucket, key) with { Data = new SingleBlob(NewBlob(size)) }, CommitCondition.Always, Ct);
        Assert.Equal(CommitStatus.Committed, commit.Status);
        return commit.Version!;
    }

    private async Task<ObjectDeletion> DeleteAsync(Bucket bucket, string key, VersionRef? version = null) =>
        (await Store.DeleteObjectVersionsAsync(bucket.Id, [new ObjectToDelete(key, version)], Ct)).Results[0];

    private async Task<List<VersionSummary>> AllVersionsAsync(Bucket bucket, string prefix = "") =>
        (await Store.ListObjectVersionsAsync(bucket.Id, prefix, null, null, null, 1000, Ct)).Versions.ToList();

    [Fact]
    public async Task Versioning_IsReadWithTheBucket_AndSetOnlyOnExistingBuckets()
    {
        var bucket = await CreateBucketAsync();
        Assert.Equal(BucketVersioning.Unversioned, (await Store.FindBucketAsync(bucket.Name, Ct))!.Versioning);

        Assert.True(await Store.SetBucketVersioningAsync(bucket.Name, BucketVersioning.Enabled, Ct));
        Assert.Equal(BucketVersioning.Enabled, (await Store.FindBucketAsync(bucket.Name, Ct))!.Versioning);
        Assert.True(await Store.SetBucketVersioningAsync(bucket.Name, BucketVersioning.Suspended, Ct));
        Assert.Equal(BucketVersioning.Suspended, (await Store.ListBucketsAsync(Ct)).Single(b => b.Id == bucket.Id).Versioning);
        Assert.False(await Store.SetBucketVersioningAsync("missing-bucket", BucketVersioning.Enabled, Ct));
    }

    [Fact]
    public async Task Enabled_EveryPutAddsAVersion_TheOldOnesKeepTheirData()
    {
        var bucket = await VersionedBucketAsync();
        var v1 = await PutAsync(bucket, "a", size: 10);
        var commit = await Store.CommitObjectAsync(NewObject(bucket, "a"), CommitCondition.Always, Ct);

        Assert.Null(commit.Replaced); // nothing released: v1 is now noncurrent
        Assert.False(commit.Version!.IsNull);
        Assert.Equal(commit.Version.Id, (await Store.FindObjectAsync(bucket.Id, "a", Ct))!.VersionId);
        var old = Assert.IsType<FoundVersion>(await Store.FindObjectVersionAsync(bucket.Id, "a", VersionRef.Of(v1.Id), Ct));
        Assert.False(old.IsLatest);
        Assert.Equal(10, old.Object.Size);
        Assert.Equal([(commit.Version.Id, true), (v1.Id, false)], (await AllVersionsAsync(bucket)).Select(v => (v.VersionId, v.IsLatest)));
    }

    [Fact]
    public async Task VersionsWrittenInOneMillisecond_StillOrderAsWritten()
    {
        var bucket = await VersionedBucketAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
            ids.Add((await PutAsync(bucket, "fast")).Id);

        ids.Reverse();
        Assert.Equal(ids, (await AllVersionsAsync(bucket)).Select(v => v.VersionId));
        Assert.Equal(ids[0], (await Store.FindObjectAsync(bucket.Id, "fast", Ct))!.VersionId);
    }

    [Fact]
    public async Task Enabled_DeleteAddsAMarker_DeletingTheMarkerRestoresTheObject()
    {
        var bucket = await VersionedBucketAsync();
        var v1 = await PutAsync(bucket, "a");

        var deleted = await DeleteAsync(bucket, "a");
        Assert.True(deleted.IsDeleteMarker);
        Assert.Null(deleted.Released);
        Assert.Null(await Store.FindObjectAsync(bucket.Id, "a", Ct));
        Assert.Empty((await Store.ListObjectsAsync(bucket.Id, "", null, null, 100, Ct)).Objects);
        var marker = Assert.IsType<FoundDeleteMarker>(await Store.FindLatestDeleteMarkerAsync(bucket.Id, "a", Ct));
        Assert.Equal(deleted.Version!.Id, marker.VersionId);
        Assert.Equal([(true, true), (false, false)], (await AllVersionsAsync(bucket)).Select(v => (v.IsDeleteMarker, v.IsLatest)));

        var removed = await DeleteAsync(bucket, "a", VersionRef.Of(marker.VersionId));
        Assert.True(removed.IsDeleteMarker);
        Assert.Equal(v1.Id, (await Store.FindObjectAsync(bucket.Id, "a", Ct))!.VersionId);
        Assert.IsType<VersionMissing>(await Store.FindLatestDeleteMarkerAsync(bucket.Id, "a", Ct));
    }

    [Fact]
    public async Task DeletingTheCurrentVersion_ReleasesItsData_AndThePreviousBecomesCurrent()
    {
        var bucket = await VersionedBucketAsync();
        var v1 = await PutAsync(bucket, "a", size: 1);
        var v2 = await PutAsync(bucket, "a", size: 2);

        var deleted = await DeleteAsync(bucket, "a", VersionRef.Of(v2.Id));

        Assert.Equal(2, deleted.Released!.Size);
        var current = (await Store.FindObjectAsync(bucket.Id, "a", Ct))!;
        Assert.Equal((v1.Id, 1L), (current.VersionId, current.Size));
        Assert.Single(await AllVersionsAsync(bucket));
    }

    [Fact]
    public async Task DeletingAVersionThatDoesNotExist_ChangesNothing()
    {
        var bucket = await VersionedBucketAsync();
        await PutAsync(bucket, "a");

        var deleted = await DeleteAsync(bucket, "a", VersionRef.Of(Guid.CreateVersion7()));

        Assert.Null(deleted.Released);
        Assert.False(deleted.IsDeleteMarker);
        Assert.Single(await AllVersionsAsync(bucket));
    }

    [Fact]
    public async Task Suspended_PutReplacesTheNullVersion_AndKeepsOtherVersions()
    {
        var bucket = await VersionedBucketAsync();
        var enabled = await PutAsync(bucket, "a", size: 5);
        Assert.True(await Store.SetBucketVersioningAsync(bucket.Name, BucketVersioning.Suspended, Ct));

        var first = await Store.CommitObjectAsync(NewObject(bucket, "a") with { Data = new SingleBlob(NewBlob(6)) }, CommitCondition.Always, Ct);
        Assert.True(first.Version!.IsNull);
        Assert.Null(first.Replaced); // the enabled version became noncurrent
        var second = await Store.CommitObjectAsync(NewObject(bucket, "a") with { Data = new SingleBlob(NewBlob(7)) }, CommitCondition.Always, Ct);
        Assert.Equal(6, second.Replaced!.Size); // the previous null version is gone

        Assert.Equal([(true, 7L), (false, 5L)], (await AllVersionsAsync(bucket)).Select(v => (v.IsNullVersion, v.Size)));
        Assert.Equal(enabled.Id, (await AllVersionsAsync(bucket))[1].VersionId);
    }

    [Fact]
    public async Task Suspended_DeleteWritesTheNullMarker_ReplacingTheNullVersion()
    {
        var bucket = await VersionedBucketAsync(BucketVersioning.Suspended);
        await PutAsync(bucket, "a", size: 9);

        var deleted = await DeleteAsync(bucket, "a");

        Assert.True(deleted.IsDeleteMarker);
        Assert.True(deleted.Version!.IsNull);
        Assert.Equal(9, deleted.Released!.Size);
        var only = Assert.Single(await AllVersionsAsync(bucket));
        Assert.True(only is { IsDeleteMarker: true, IsNullVersion: true, IsLatest: true });

        Assert.Single((await DeleteAsync(bucket, "a")) is { IsDeleteMarker: true } ? await AllVersionsAsync(bucket) : []);
    }

    [Fact]
    public async Task ObjectsWrittenBeforeVersioning_AreTheNullVersion()
    {
        var bucket = await CreateBucketAsync();
        await PutAsync(bucket, "a", size: 3);
        Assert.True(await Store.SetBucketVersioningAsync(bucket.Name, BucketVersioning.Enabled, Ct));
        await PutAsync(bucket, "a", size: 4);

        var old = Assert.IsType<FoundVersion>(await Store.FindObjectVersionAsync(bucket.Id, "a", VersionRef.Null, Ct));
        Assert.Equal(3, old.Object.Size);
        Assert.True(old.Object.IsNullVersion);
    }

    [Fact]
    public async Task ListObjectVersions_PagesWithKeyAndVersionMarkers()
    {
        var bucket = await VersionedBucketAsync();
        foreach (var key in new[] { "a", "a", "a", "b", "c", "c" })
            await PutAsync(bucket, key);
        await DeleteAsync(bucket, "b");
        var all = await AllVersionsAsync(bucket);
        Assert.Equal(["a", "a", "a", "b", "b", "c", "c"], all.Select(v => v.Key));

        var seen = new List<VersionSummary>();
        string? keyMarker = null;
        VersionRef? versionMarker = null;
        while (true)
        {
            var page = await Store.ListObjectVersionsAsync(bucket.Id, "", null, keyMarker, versionMarker, 2, Ct);
            seen.AddRange(page.Versions);
            if (!page.IsTruncated)
                break;
            keyMarker = page.NextKeyMarker;
            Assert.True(VersionIds.TryParse(page.NextVersionIdMarker, out var next));
            versionMarker = next;
        }

        Assert.Equal(all.Select(v => (v.Key, v.VersionId, v.IsLatest)), seen.Select(v => (v.Key, v.VersionId, v.IsLatest)));
    }

    [Fact]
    public async Task ListObjectVersions_RollsUpCommonPrefixes()
    {
        var bucket = await VersionedBucketAsync();
        foreach (var key in new[] { "photos/1", "photos/1", "photos/2", "top" })
            await PutAsync(bucket, key);

        var page = await Store.ListObjectVersionsAsync(bucket.Id, "", "/", null, null, 100, Ct);

        Assert.Equal(["photos/"], page.CommonPrefixes);
        Assert.Equal(["top"], page.Versions.Select(v => v.Key));
    }

    [Fact]
    public async Task ABucketWithOnlyVersions_IsNotEmpty_AndNoncurrentBlobsAreReferenced()
    {
        var bucket = await VersionedBucketAsync();
        await Store.CommitObjectAsync(NewObject(bucket, "a"), CommitCondition.Always, Ct);
        var noncurrent = BlobOf((await Store.FindObjectAsync(bucket.Id, "a", Ct))!);
        await DeleteAsync(bucket, "a");

        Assert.Empty(await Store.FindUnreferencedBlobsAsync([noncurrent.BlobId], Ct));
        Assert.Equal(BucketDeletion.NotEmpty, (await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct)).Status);
    }

    [Fact]
    public async Task Usage_CountsNoncurrentVersionsSeparately()
    {
        var bucket = await VersionedBucketAsync();
        await PutAsync(bucket, "a", size: 10);
        await PutAsync(bucket, "a", size: 20);
        await DeleteAsync(bucket, "a");

        var usage = (await Store.ListBucketUsageAsync(Ct)).Single(u => u.BucketId == bucket.Id);

        Assert.Equal((0L, 0L, 3L, 30L), (usage.ObjectCount, usage.TotalBytes, usage.VersionCount, usage.VersionBytes));
    }

    [Fact]
    public async Task Enabled_MultipartCompletion_KeepsThePreviousVersion()
    {
        var bucket = await VersionedBucketAsync();
        var before = await PutAsync(bucket, "movie.mp4", size: 3);
        var upload = await CreateUploadAsync(bucket);
        var part = NewPart(upload, 1, size: 30);
        await Store.CommitPartAsync(part, Ct);

        var completion = await Store.CompleteUploadAsync(Manifest(upload, part), CommitCondition.Always, Ct);

        Assert.Null(completion.Replaced);
        Assert.False(completion.Version!.IsNull);
        Assert.IsType<FoundVersion>(await Store.FindObjectVersionAsync(bucket.Id, "movie.mp4", VersionRef.Of(before.Id), Ct));
        Assert.Equal(30, (await Store.FindObjectAsync(bucket.Id, "movie.mp4", Ct))!.Size);
    }

    // --- Lifecycle (step 6c) ----------------------------------------------------------------------------------------

    private static LifecycleRule Rule(string id, string prefix = "", bool enabled = true) => new(
        id, enabled, new LifecycleFilter(prefix, 10, 1000), ExpirationDays: 30,
        ExpirationDate: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), ExpiredObjectDeleteMarker: false,
        NoncurrentDays: 7, NewerNoncurrentVersions: 2, AbortIncompleteUploadDays: 3);

    [Fact]
    public async Task Lifecycle_RoundTripsInOrder_ReplacesWhole_AndGoesWithTheBucket()
    {
        var bucket = await CreateBucketAsync();
        Assert.Null(await Store.GetLifecycleAsync(bucket.Id, Ct));

        var legacy = Rule("a", "tmp/", enabled: false) with { Filter = new LifecycleFilter("tmp/", null, null, PrefixInRule: true) };
        Assert.True(await Store.SetLifecycleAsync(bucket.Id, [Rule("b", "logs/"), legacy], Ct));
        Assert.Equal([Rule("b", "logs/"), legacy], await Store.GetLifecycleAsync(bucket.Id, Ct));

        Assert.True(await Store.SetLifecycleAsync(bucket.Id, [Rule("only")], Ct));
        Assert.Equal(["only"], (await Store.GetLifecycleAsync(bucket.Id, Ct))!.Select(r => r.Id));

        await Store.DeleteLifecycleAsync(bucket.Id, Ct);
        Assert.Null(await Store.GetLifecycleAsync(bucket.Id, Ct));

        await Store.SetLifecycleAsync(bucket.Id, [Rule("x")], Ct);
        await Store.DeleteBucketIfEmptyAsync(bucket.Name, Ct);
        Assert.Null(await Store.GetLifecycleAsync(bucket.Id, Ct));
        Assert.False(await Store.SetLifecycleAsync(bucket.Id, [Rule("x")], Ct));
    }

    [Fact]
    public async Task BucketsWithLifecycle_AreThoseWithAnEnabledRule()
    {
        var active = await CreateBucketAsync();
        var disabled = await CreateBucketAsync();
        await Store.SetLifecycleAsync(active.Id, [Rule("on")], Ct);
        await Store.SetLifecycleAsync(disabled.Id, [Rule("off", enabled: false)], Ct);

        var ids = await Store.ListBucketsWithLifecycleAsync(Ct);

        Assert.Contains(active.Id, ids);
        Assert.DoesNotContain(disabled.Id, ids);
    }

    [Fact]
    public async Task VersionListing_ReportsWhenAVersionBecameNoncurrent()
    {
        var bucket = await VersionedBucketAsync();
        await PutAsync(bucket, "a");
        await Store.CommitObjectAsync(NewObject(bucket, "a") with { CreatedAt = Timestamp.AddDays(1) }, CommitCondition.Always, Ct);

        var versions = await AllVersionsAsync(bucket);

        Assert.Null(versions[0].SupersededAt);
        Assert.Equal(Timestamp.AddDays(1), versions[1].SupersededAt);
    }

    // --- Usage (step 7e) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task BucketUsage_FollowsEveryObjectWrite()
    {
        var bucket = await CreateBucketAsync();
        Assert.Equal((0L, 0L), await UsageOf(bucket));

        await Store.CommitObjectAsync(NewObject(bucket, "a"), CommitCondition.Always, Ct); // 42 bytes
        await Store.CommitObjectAsync(NewObject(bucket, "b"), CommitCondition.Always, Ct);
        Assert.Equal((2L, 84L), await UsageOf(bucket));

        var bigger = NewObject(bucket, "a") with { Data = new SingleBlob(NewBlob(100)) };
        await Store.CommitObjectAsync(bigger, CommitCondition.Always, Ct); // replace: count stays, bytes change
        Assert.Equal((2L, 142L), await UsageOf(bucket));

        await Store.DeleteObjectAsync(bucket.Id, "b", Ct);
        Assert.Equal((1L, 100L), await UsageOf(bucket));

        await Store.DeleteObjectsAsync(bucket.Id, ["a", "missing"], Ct);
        Assert.Equal((0L, 0L), await UsageOf(bucket));
    }

    [Fact]
    public async Task Usage_CountsUploadsUntilCompleted_ThenTheObject()
    {
        var bucket = await CreateBucketAsync();
        var upload = await CreateUploadAsync(bucket);
        var one = NewPart(upload, 1, size: 30);
        var two = NewPart(upload, 2, size: 12);
        await Store.CommitPartAsync(one, Ct);
        await Store.CommitPartAsync(two, Ct);
        Assert.Equal(new UploadUsage(1, 2, 42), await Store.GetUploadUsageAsync(Ct));
        Assert.Equal((0L, 0L), await UsageOf(bucket));

        await Store.CompleteUploadAsync(Manifest(upload, one, two), CommitCondition.Always, Ct);

        Assert.Equal(new UploadUsage(0, 0, 0), await Store.GetUploadUsageAsync(Ct));
        Assert.Equal((1L, 42L), await UsageOf(bucket));
    }

    [Fact]
    public async Task BucketUsage_ListsBucketsByName_AndForgetsDeletedBuckets()
    {
        await Store.TryCreateBucketAsync(NewBucket("zeta"), Ct);
        await Store.TryCreateBucketAsync(NewBucket("alpha"), Ct);
        await Store.DeleteBucketIfEmptyAsync("zeta", Ct);

        var usage = await Store.ListBucketUsageAsync(Ct);

        Assert.Equal(["alpha"], usage.Select(u => u.BucketName));
    }

    private async Task<(long Count, long Bytes)> UsageOf(Bucket bucket)
    {
        var usage = (await Store.ListBucketUsageAsync(Ct)).Single(u => u.BucketId == bucket.Id);
        return (usage.ObjectCount, usage.TotalBytes);
    }

    // --- Helpers -------------------------------------------------------------------------------------------------

    private async Task<Bucket> CreateBucketAsync()
    {
        var bucket = NewBucket($"b{Guid.NewGuid():N}"[..20]);
        Assert.True(await Store.TryCreateBucketAsync(bucket, Ct));
        return bucket;
    }

    // Millisecond precision: providers store unix milliseconds.
    private static readonly DateTime Timestamp = new(2026, 11, 16, 9, 30, 15, 123, DateTimeKind.Utc);

    private static Bucket NewBucket(string name) => new(Guid.CreateVersion7(), name, Timestamp);

    // --- Released blobs (B5a) --------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryWriteThatReleasesABlob_RecordsIt()
    {
        var bucket = await CreateBucketAsync();
        var overwritten = NewObject(bucket, "a");
        await Store.CommitObjectAsync(overwritten, CommitCondition.Always, Ct);
        await Store.CommitObjectAsync(NewObject(bucket, "a"), CommitCondition.Always, Ct); // overwrite
        var deleted = NewObject(bucket, "b");
        await Store.CommitObjectAsync(deleted, CommitCondition.Always, Ct);
        await Store.DeleteObjectAsync(bucket.Id, "b", Ct);
        var batchDeleted = NewObject(bucket, "c");
        await Store.CommitObjectAsync(batchDeleted, CommitCondition.Always, Ct);
        await Store.DeleteObjectsAsync(bucket.Id, ["c"], Ct);

        var upload = await CreateUploadAsync(bucket, "m");
        var replacedPart = NewPart(upload, 1);
        await Store.CommitPartAsync(replacedPart, Ct);
        var part1 = NewPart(upload, 1);
        await Store.CommitPartAsync(part1, Ct); // replaces part 1
        var unlisted = NewPart(upload, 2);
        await Store.CommitPartAsync(unlisted, Ct);
        await Store.CompleteUploadAsync(Manifest(upload, part1), CommitCondition.Always, Ct);

        var aborted = await CreateUploadAsync(bucket, "x");
        var abortedPart = NewPart(aborted, 1);
        await Store.CommitPartAsync(abortedPart, Ct);
        await Store.AbortUploadAsync(bucket.Id, "x", aborted.UploadId, Ct);

        var empty = NewBucket("empty-with-upload");
        await Store.TryCreateBucketAsync(empty, Ct);
        var open = await CreateUploadAsync(empty, "y");
        var openPart = NewPart(open, 1);
        await Store.CommitPartAsync(openPart, Ct);
        await Store.DeleteBucketIfEmptyAsync(empty.Name, Ct);

        var released = await Store.ListReleasedBlobsAsync("node-1", DateTime.UtcNow.AddMinutes(1), 100, Ct);
        Assert.Equal(
            new[] { BlobOf(overwritten), BlobOf(deleted), BlobOf(batchDeleted), replacedPart.Blob, unlisted.Blob, abortedPart.Blob, openPart.Blob }
                .Select(static b => b.BlobId).Order(),
            released.Select(static b => b.BlobId).Order());
        Assert.Equal(new RetainedBlobs(7, 42 * 3 + 10 * 4), await Store.GetRetainedBlobsAsync(Ct));
    }

    [Fact]
    public async Task ReleasedBlobs_AreListedPerNodeOldestFirst_CountAsReferenced_AndAreForgotten()
    {
        var bucket = await CreateBucketAsync();
        var first = NewObject(bucket, "a");
        await Store.CommitObjectAsync(first, CommitCondition.Always, Ct);
        await Store.DeleteObjectAsync(bucket.Id, "a", Ct);

        Assert.Empty(await Store.ListReleasedBlobsAsync("node-2", DateTime.UtcNow.AddMinutes(1), 10, Ct));
        Assert.Empty(await Store.ListReleasedBlobsAsync("node-1", DateTime.UtcNow.AddDays(-1), 10, Ct));
        var listed = Assert.Single(await Store.ListReleasedBlobsAsync("node-1", DateTime.UtcNow.AddMinutes(1), 10, Ct));
        Assert.Equal((BlobOf(first).BlobId, 42L), (listed.BlobId, listed.Size));

        // Waiting for its collector: the orphan sweep must not take it.
        Assert.Empty(await Store.FindUnreferencedBlobsAsync([BlobOf(first).BlobId], Ct));

        await Store.ForgetReleasedBlobsAsync([BlobOf(first).BlobId], Ct);
        Assert.Equal(default, await Store.GetRetainedBlobsAsync(Ct));
        Assert.Equal([BlobOf(first).BlobId], await Store.FindUnreferencedBlobsAsync([BlobOf(first).BlobId], Ct));
    }

    /// <summary>Ids of every released blob waiting for its collector, ordered.</summary>
    private async Task<Guid[]> ReleasedIdsAsync() =>
        [.. (await Store.ListReleasedBlobsAsync("node-1", DateTime.UtcNow.AddYears(1), int.MaxValue, Ct)).Select(static b => b.BlobId).Order()];

    private static StoredObject NewObject(Bucket bucket, string key) => new(
        bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(NewBlob(42)),
        "d41d8cd98f00b204e9800998ecf8427e", StoredObject.DefaultContentType, null, null, null, [], [], Timestamp);

    private static BlobLocator NewBlob(long size) =>
        new(Guid.CreateVersion7(), "node-1", size, "d41d8cd98f00b204e9800998ecf8427e");

    private static BlobLocator BlobOf(StoredObject stored) => ((SingleBlob)stored.Data).Blob;
}
