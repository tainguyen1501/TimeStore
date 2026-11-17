using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>Step 6a: bucket versioning and delete markers end to end, with the AWS SDK as the client.</summary>
public sealed class VersioningTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"ver-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task SetVersioningAsync(VersionStatus status) =>
        _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = status },
        }, _ct);

    private async Task<string> PutAsync(string key, string body) =>
        (await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = body }, _ct)).VersionId;

    private async Task<string> GetAsync(string key, string? versionId = null)
    {
        using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key, VersionId = versionId }, _ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(_ct);
    }

    [Fact]
    public async Task GetBucketVersioning_FollowsPutBucketVersioning()
    {
        // A never-versioned bucket answers an empty configuration, which the SDK reports as "Off".
        Assert.Equal(VersionStatus.Off, (await _s3.GetBucketVersioningAsync(_bucket, _ct)).VersioningConfig.Status);
        await SetVersioningAsync(VersionStatus.Enabled);
        Assert.Equal(VersionStatus.Enabled, (await _s3.GetBucketVersioningAsync(_bucket, _ct)).VersioningConfig.Status);
        await SetVersioningAsync(VersionStatus.Suspended);
        Assert.Equal(VersionStatus.Suspended, (await _s3.GetBucketVersioningAsync(_bucket, _ct)).VersioningConfig.Status);
    }

    [Fact]
    public async Task Enabled_KeepsEveryVersion_ReadableById()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var v1 = await PutAsync("doc.txt", "one");
        var v2 = await PutAsync("doc.txt", "two");

        Assert.NotEqual(v1, v2);
        Assert.Matches("^[0-9a-f]{32}$", v1);
        Assert.Equal("two", await GetAsync("doc.txt"));
        Assert.Equal("one", await GetAsync("doc.txt", v1));
        var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = "doc.txt", VersionId = v1 }, _ct);
        Assert.Equal(v1, head.VersionId);
    }

    [Fact]
    public async Task DeleteAddsAMarker_GetIs404_TheMarkerByIdIs405_RemovingItRestores()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var v1 = await PutAsync("gone.txt", "data");

        var deleted = await _s3.DeleteObjectAsync(_bucket, "gone.txt", _ct);
        Assert.Equal("true", deleted.DeleteMarker);
        var marker = deleted.VersionId;
        Assert.NotEqual(v1, marker);

        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => GetAsync("gone.txt"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var notAllowed = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = "gone.txt", VersionId = marker }, _ct));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, notAllowed.StatusCode);
        Assert.Empty((await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = "gone" }, _ct)).S3Objects ?? []);

        var removed = await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = "gone.txt", VersionId = marker }, _ct);
        Assert.Equal("true", removed.DeleteMarker);
        Assert.Equal(marker, removed.VersionId);
        Assert.Equal("data", await GetAsync("gone.txt"));
    }

    [Fact]
    public async Task ListVersions_ShowsVersionsAndMarkers_NewestFirst()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var v1 = await PutAsync("l.txt", "1");
        var v2 = await PutAsync("l.txt", "2");
        var marker = (await _s3.DeleteObjectAsync(_bucket, "l.txt", _ct)).VersionId;

        var listed = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = "l.txt" }, _ct);

        Assert.Equal([marker, v2, v1], listed.Versions.Select(v => v.VersionId));
        Assert.Equal([true, false, false], listed.Versions.Select(v => v.IsDeleteMarker ?? false));
        Assert.Equal([true, false, false], listed.Versions.Select(v => v.IsLatest ?? false));
    }

    [Fact]
    public async Task ListVersions_Pages_WithKeyAndVersionMarkers()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        for (var i = 0; i < 3; i++)
        {
            await PutAsync("p/a", $"a{i}");
            await PutAsync("p/b", $"b{i}");
        }

        var seen = new List<string>();
        var request = new ListVersionsRequest { BucketName = _bucket, Prefix = "p/", MaxKeys = 4 };
        while (true)
        {
            var page = await _s3.ListVersionsAsync(request, _ct);
            seen.AddRange(page.Versions.Select(v => $"{v.Key}:{v.VersionId}"));
            if (page.IsTruncated != true)
                break;
            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = page.NextVersionIdMarker;
        }

        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public async Task Suspended_WritesTheNullVersion_AndReplacesIt()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var kept = await PutAsync("s.txt", "enabled");
        await SetVersioningAsync(VersionStatus.Suspended);

        // Writes in a suspended bucket answer no version id (they write the null version).
        Assert.Null(await PutAsync("s.txt", "first null"));
        Assert.Null(await PutAsync("s.txt", "second null"));

        Assert.Equal("second null", await GetAsync("s.txt", "null"));
        Assert.Equal("enabled", await GetAsync("s.txt", kept));
        var listed = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = "s.txt" }, _ct);
        Assert.Equal(["null", kept], listed.Versions.Select(v => v.VersionId));
    }

    [Fact]
    public async Task DeleteObjects_WithVersions_ReportsMarkersAndVersions()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var v1 = await PutAsync("m1", "x");
        await PutAsync("m2", "y");

        // A per-key error makes the SDK throw; the parsed result travels in the exception.
        var response = (await Assert.ThrowsAsync<DeleteObjectsException>(() => _s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = _bucket,
            Objects = [new KeyVersion { Key = "m1", VersionId = v1 }, new KeyVersion { Key = "m2" }, new KeyVersion { Key = "m3", VersionId = "not-a-version" }],
        }, _ct))).Response;

        var byKey = response.DeletedObjects.ToDictionary(d => d.Key);
        Assert.Equal(v1, byKey["m1"].VersionId);
        Assert.NotEqual(true, byKey["m1"].DeleteMarker);
        Assert.True(byKey["m2"].DeleteMarker);
        Assert.Matches("^[0-9a-f]{32}$", byKey["m2"].DeleteMarkerVersionId);
        Assert.Equal("InvalidArgument", Assert.Single(response.DeleteErrors).Code);
    }

    [Fact]
    public async Task CopyFromAVersion_NamesBothVersions()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var source = await PutAsync("src.txt", "old");
        await PutAsync("src.txt", "new");

        var copy = await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "src.txt", SourceVersionId = source,
            DestinationBucket = _bucket, DestinationKey = "dst.txt",
        }, _ct);

        Assert.Equal(source, copy.SourceVersionId);
        Assert.Matches("^[0-9a-f]{32}$", copy.VersionId);
        Assert.Equal("old", await GetAsync("dst.txt"));
    }

    [Fact]
    public async Task CopyingAnOlderVersionOverItsKey_RestoresIt_TheCurrentOneOntoItselfIsRefused()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var old = await PutAsync("r.txt", "old");
        var current = await PutAsync("r.txt", "new");

        var restored = await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "r.txt", SourceVersionId = old, DestinationBucket = _bucket, DestinationKey = "r.txt",
        }, _ct);
        Assert.Equal("old", await GetAsync("r.txt"));
        Assert.NotEqual(current, restored.VersionId);

        var refused = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket, SourceKey = "r.txt", DestinationBucket = _bucket, DestinationKey = "r.txt",
        }, _ct));
        Assert.Equal("InvalidRequest", refused.ErrorCode);
    }

    [Fact]
    public async Task MultipartCompletion_ReturnsTheVersion()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var upload = await _s3.InitiateMultipartUploadAsync(_bucket, "mp.bin", _ct);
        var part = await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket, Key = "mp.bin", UploadId = upload.UploadId, PartNumber = 1,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("only part")),
        }, _ct);

        var completed = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = "mp.bin", UploadId = upload.UploadId, PartETags = [new PartETag(1, part.ETag)],
        }, _ct);

        Assert.Matches("^[0-9a-f]{32}$", completed.VersionId);
    }

    [Fact]
    public async Task VersionIds_Malformed400_Unknown404()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        await PutAsync("v.txt", "x");

        var malformed = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => GetAsync("v.txt", "bad-id"));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArgument"), (malformed.StatusCode, malformed.ErrorCode));
        var unknown = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => GetAsync("v.txt", Guid.CreateVersion7().ToString("N")));
        Assert.Equal((HttpStatusCode.NotFound, "NoSuchVersion"), (unknown.StatusCode, unknown.ErrorCode));
    }

    [Fact]
    public async Task ABucketWithVersions_IsNotEmpty_UntilEveryVersionIsGone()
    {
        await SetVersioningAsync(VersionStatus.Enabled);
        var v1 = await PutAsync("last.txt", "x");
        var marker = (await _s3.DeleteObjectAsync(_bucket, "last.txt", _ct)).VersionId;

        var notEmpty = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.DeleteBucketAsync(_bucket, _ct));
        Assert.Equal("BucketNotEmpty", notEmpty.ErrorCode);

        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = "last.txt", VersionId = marker }, _ct);
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = "last.txt", VersionId = v1 }, _ct);
        await _s3.DeleteBucketAsync(_bucket, _ct);
    }

    [Fact]
    public async Task MfaDelete_CannotBeEnabled()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled, EnableMfaDelete = true },
        }, _ct));
        Assert.Equal("NotImplemented", error.ErrorCode);
    }
}
