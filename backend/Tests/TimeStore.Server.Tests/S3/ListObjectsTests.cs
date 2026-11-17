using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>
/// Real listings end to end: SDK paginators across many small pages, delimiters, v1 markers, special keys. Expected
/// order is UTF-8 byte order, as Amazon S3 lists.
/// </summary>
public sealed class ListObjectsTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly string[] Keys =
    [
        .. Enumerable.Range(1, 12).Select(i => $"logs/2026/{i:D2}.log"),
        "logs/readme.txt", "photos/cat.jpg", "photos/ảnh 1.jpg", "a.txt", "z.txt", "space name.txt", "plus+key",
        "amp&key", "q?mark", "percent%20literal", "deep/a/b/c/d.txt",
    ];

    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"e2e-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync()
    {
        await _s3.PutBucketAsync(_bucket, _ct);
        foreach (var key in Keys)
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = key }, _ct);
    }

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> Utf8Order(IEnumerable<string> keys) =>
        keys.Order(Comparer<string>.Create((a, b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b))));

    [Theory]
    [InlineData(1000)]
    [InlineData(7)]
    [InlineData(1)]
    public async Task ListV2_Paginated_ReturnsEveryKeyOnceInOrder(int pageSize)
    {
        var keys = new List<string>();
        var pages = 0;
        var request = new ListObjectsV2Request { BucketName = _bucket, MaxKeys = pageSize };
        await foreach (var page in _s3.Paginators.ListObjectsV2(request).Responses.WithCancellation(_ct))
        {
            pages++;
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
        }

        Assert.Equal(Utf8Order(Keys), keys);
        Assert.Equal((Keys.Length + pageSize - 1) / pageSize, pages);
    }

    [Fact]
    public async Task ListV2_Delimiter_RollsUpPrefixes_AcrossPages()
    {
        var keys = new List<string>();
        var prefixes = new List<string>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Delimiter = "/", MaxKeys = 3 };
        await foreach (var page in _s3.Paginators.ListObjectsV2(request).Responses.WithCancellation(_ct))
        {
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            prefixes.AddRange(page.CommonPrefixes ?? []);
        }

        Assert.Equal(Utf8Order(Keys.Where(k => !k.Contains('/'))), keys);
        Assert.Equal(new[] { "deep/", "logs/", "photos/" }, prefixes);
    }

    [Fact]
    public async Task ListV2_PrefixAndDelimiter_ListsOneLevel()
    {
        var response = await _s3.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = _bucket, Prefix = "logs/", Delimiter = "/" }, _ct);

        Assert.Equal(new[] { "logs/readme.txt" }, response.S3Objects.Select(o => o.Key));
        Assert.Equal(new[] { "logs/2026/" }, response.CommonPrefixes);
        Assert.Equal(2, response.KeyCount);
    }

    [Fact]
    public async Task ListV2_StartAfter_SkipsEarlierKeys()
    {
        var response = await _s3.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = _bucket, Prefix = "logs/2026/", StartAfter = "logs/2026/10.log" }, _ct);

        Assert.Equal(new[] { "logs/2026/11.log", "logs/2026/12.log" }, response.S3Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task ListV2_ReturnsSizeETagAndDate()
    {
        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = "a.txt" }, _ct);

        var entry = Assert.Single(response.S3Objects);
        Assert.Equal("a.txt".Length, entry.Size);
        Assert.Equal((await _s3.GetObjectMetadataAsync(_bucket, "a.txt", _ct)).ETag, entry.ETag);
        Assert.True(DateTime.UtcNow - entry.LastModified!.Value.ToUniversalTime() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ListV1_MarkerPaging_ReturnsEveryKeyOnce()
    {
        var keys = new List<string>();
        string? marker = null;
        while (true)
        {
            var page = await _s3.ListObjectsAsync(new ListObjectsRequest { BucketName = _bucket, MaxKeys = 5, Marker = marker }, _ct);
            keys.AddRange(page.S3Objects.Select(o => o.Key));
            if (page.IsTruncated != true)
                break;
            marker = page.NextMarker ?? page.S3Objects[^1].Key; // NextMarker only comes with a delimiter
        }

        Assert.Equal(Utf8Order(Keys), keys);
    }

    [Fact]
    public async Task ListV2_ForeignContinuationToken_IsInvalidArgument()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = _bucket, ContinuationToken = "not a token!" }, _ct));

        Assert.Equal("InvalidArgument", error.ErrorCode);
    }

    // --- ListObjectVersions on a never-versioned bucket (step 4d) ------------------------------------------------

    [Fact]
    public async Task ListVersions_ShowsEveryObjectAsItsOnlyNullVersion_AndPages()
    {
        var bucket = $"e2e-{Guid.NewGuid():N}";
        await _s3.PutBucketAsync(bucket, _ct);
        foreach (var key in new[] { "a", "b", "c/1", "c/2", "d" })
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = key }, _ct);

        var first = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, MaxKeys = 2 }, _ct);
        var rest = await _s3.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = bucket, KeyMarker = first.NextKeyMarker, VersionIdMarker = first.NextVersionIdMarker,
        }, _ct);
        var rolled = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Delimiter = "/" }, _ct);

        Assert.True(first.IsTruncated);
        Assert.Equal(["a", "b"], first.Versions.Select(v => v.Key));
        Assert.All(first.Versions, v => Assert.Equal(("null", true), (v.VersionId, v.IsLatest ?? false)));
        Assert.Equal(("b", "null"), (first.NextKeyMarker, first.NextVersionIdMarker));
        Assert.Equal(["c/1", "c/2", "d"], rest.Versions.Select(v => v.Key));
        Assert.False(rest.IsTruncated ?? false);
        Assert.Equal(["a", "b", "d"], rolled.Versions.Select(v => v.Key));
        Assert.Equal(["c/"], rolled.CommonPrefixes);
    }

    [Fact]
    public async Task ListVersions_ThenDeleteObjectsByVersion_EmptiesTheBucket()
    {
        // How generic cleanup code (e.g. the ceph s3-tests teardown) empties a bucket.
        var bucket = $"e2e-{Guid.NewGuid():N}";
        await _s3.PutBucketAsync(bucket, _ct);
        foreach (var key in new[] { "x", "y/z" })
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = key }, _ct);

        var versions = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket }, _ct);
        await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket, Quiet = true,
            Objects = [.. versions.Versions.Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId })],
        }, _ct);
        await _s3.DeleteBucketAsync(bucket, _ct);
    }
}
