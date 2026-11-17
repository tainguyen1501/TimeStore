using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>Multipart operations before completion (step 3b), driven by the AWS SDK for .NET.</summary>
public sealed class MultipartUploadTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IDisposable
{
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _s3.Dispose();

    private async Task<string> NewBucketAsync()
    {
        var bucket = $"mp-{Guid.NewGuid():N}"[..30];
        await _s3.PutBucketAsync(bucket, _ct);
        return bucket;
    }

    private Task<InitiateMultipartUploadResponse> InitiateAsync(string bucket, string key, ChecksumAlgorithm? algorithm = null) =>
        _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = key, ContentType = "video/mp4", ChecksumAlgorithm = algorithm,
        }, _ct);

    private Task<UploadPartResponse> UploadPartAsync(string bucket, string key, string uploadId, int number, byte[] data) =>
        _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data),
        }, _ct);

    [Fact]
    public async Task Initiate_UploadParts_ListParts_Abort()
    {
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "movie.mp4");
        Assert.Matches("^[0-9a-f]{32}$", upload.UploadId);

        var etags = new List<string>();
        for (var number = 1; number <= 3; number++)
            etags.Add((await UploadPartAsync(bucket, "movie.mp4", upload.UploadId, number, new byte[number * 1000])).ETag);

        var first = await _s3.ListPartsAsync(new ListPartsRequest
        {
            BucketName = bucket, Key = "movie.mp4", UploadId = upload.UploadId, MaxParts = 2,
        }, _ct);
        Assert.Equal([1, 2], first.Parts.Select(p => p.PartNumber!.Value));
        Assert.Equal([1000L, 2000L], first.Parts.Select(p => p.Size!.Value));
        Assert.Equal(etags.Take(2), first.Parts.Select(p => p.ETag));
        Assert.True(first.IsTruncated);
        var rest = await _s3.ListPartsAsync(new ListPartsRequest
        {
            BucketName = bucket, Key = "movie.mp4", UploadId = upload.UploadId, PartNumberMarker = first.NextPartNumberMarker.ToString(),
        }, _ct);
        Assert.Equal([3], rest.Parts.Select(p => p.PartNumber!.Value));
        Assert.False(rest.IsTruncated ?? false);

        var aborted = await _s3.AbortMultipartUploadAsync(bucket, "movie.mp4", upload.UploadId, _ct);
        Assert.Equal(HttpStatusCode.NoContent, aborted.HttpStatusCode);
        var gone = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = "movie.mp4", UploadId = upload.UploadId }, _ct));
        Assert.Equal("NoSuchUpload", gone.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task UploadPart_WithAChecksumAlgorithm_IsVerifiedAndListed()
    {
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "data.bin", ChecksumAlgorithm.CRC32C);
        Assert.Equal(ChecksumAlgorithm.CRC32C, upload.ChecksumAlgorithm);

        var part = await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = "data.bin", UploadId = upload.UploadId, PartNumber = 1,
            InputStream = new MemoryStream("hello multipart"u8.ToArray()), ChecksumAlgorithm = ChecksumAlgorithm.CRC32C,
        }, _ct);
        Assert.False(string.IsNullOrEmpty(part.ChecksumCRC32C));

        var parts = await _s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = "data.bin", UploadId = upload.UploadId }, _ct);
        Assert.Equal(part.ChecksumCRC32C, Assert.Single(parts.Parts).ChecksumCRC32C);
        Assert.Equal(ChecksumAlgorithm.CRC32C, parts.ChecksumAlgorithm);
    }

    [Fact]
    public async Task UploadPart_ToAnUnknownUpload_IsNoSuchUpload()
    {
        var bucket = await NewBucketAsync();

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            UploadPartAsync(bucket, "a", Guid.CreateVersion7().ToString("N"), 1, [1, 2, 3]));

        Assert.Equal("NoSuchUpload", error.ErrorCode);
    }

    [Fact]
    public async Task ListMultipartUploads_ByKeyThenInitiation_WithDelimiter()
    {
        var bucket = await NewBucketAsync();
        var b1 = await InitiateAsync(bucket, "b.bin");
        var a = await InitiateAsync(bucket, "a.bin");
        var b2 = await InitiateAsync(bucket, "b.bin");
        await InitiateAsync(bucket, "photos/1.jpg");
        // Same key: upload id order (initiation time to the millisecond; ties have no defined order).
        if (string.CompareOrdinal(b1.UploadId, b2.UploadId) > 0)
            (b1, b2) = (b2, b1);

        var all = await _s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Delimiter = "/" }, _ct);
        Assert.Equal([a.UploadId, b1.UploadId, b2.UploadId], all.MultipartUploads.Select(u => u.UploadId));
        Assert.Equal(["photos/"], all.CommonPrefixes);

        var page = await _s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, MaxUploads = 2 }, _ct);
        Assert.True(page.IsTruncated);
        var next = await _s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = bucket, KeyMarker = page.NextKeyMarker, UploadIdMarker = page.NextUploadIdMarker,
        }, _ct);
        Assert.Equal([b2.UploadId, "photos/1.jpg"], next.MultipartUploads.Select(u => u.Key == "b.bin" ? u.UploadId : u.Key));
    }

    [Fact]
    public async Task PendingUploads_AreAbortedWithTheirBucket()
    {
        // Amazon S3: only objects keep a general purpose bucket from being deleted (ceph s3-tests rely on it).
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "pending");
        var existing = BlobFiles();
        await UploadPartAsync(bucket, "pending", upload.UploadId, 1, new byte[1000]);
        var part = BlobFiles().Except(existing).ToArray(); // this test's blob (tests of a class run one at a time)

        await _s3.DeleteBucketAsync(bucket, _ct);

        Assert.Single(part);
        await TimeStoreServer.WaitUntilAsync(() => !File.Exists(part[0]), _ct); // freed after the deletion grace
        Assert.False(File.Exists(part[0])); // the part blob went with the upload
        var gone = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.ListMultipartUploadsAsync(bucket, _ct));
        Assert.Equal("NoSuchBucket", gone.ErrorCode);
    }

    [Fact]
    public async Task AnObject_KeepsABucketWithUploadsFromBeingDeleted()
    {
        var bucket = await NewBucketAsync();
        await InitiateAsync(bucket, "pending");
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "kept", ContentBody = "x" }, _ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.DeleteBucketAsync(bucket, _ct));

        Assert.Equal("BucketNotEmpty", error.ErrorCode);
    }

    // --- CompleteMultipartUpload (step 3c) -------------------------------------------------------------------------

    private static readonly byte[] FivePlusMiB = new byte[5 * 1024 * 1024];

    [Fact]
    public async Task Complete_CommitsTheObject_WithAnMd5NETag_AndARetrySucceeds()
    {
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "movie.mp4");
        var p1 = await UploadPartAsync(bucket, "movie.mp4", upload.UploadId, 1, FivePlusMiB);
        var p2 = await UploadPartAsync(bucket, "movie.mp4", upload.UploadId, 2, [1, 2, 3]);
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "movie.mp4", UploadId = upload.UploadId,
            PartETags = [new PartETag(1, p1.ETag), new PartETag(2, p2.ETag)],
        };

        var completed = await _s3.CompleteMultipartUploadAsync(request, _ct);
        var retried = await _s3.CompleteMultipartUploadAsync(request, _ct);

        Assert.EndsWith("-2\"", completed.ETag);
        Assert.Equal(completed.ETag, retried.ETag);
        Assert.EndsWith($"/{bucket}/movie.mp4", completed.Location);
        var head = await _s3.GetObjectMetadataAsync(bucket, "movie.mp4", _ct);
        Assert.Equal(FivePlusMiB.Length + 3, head.ContentLength);
        Assert.Equal(completed.ETag, head.ETag);
        Assert.Equal("video/mp4", head.Headers.ContentType);
        var listed = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, _ct);
        Assert.Equal(FivePlusMiB.Length + 3, Assert.Single(listed.S3Objects).Size);
        var uploads = await _s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket }, _ct);
        Assert.Empty(uploads.MultipartUploads ?? []);
    }

    [Fact]
    public async Task Complete_ErrorsMatchAmazonS3()
    {
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "a.bin");
        var small = await UploadPartAsync(bucket, "a.bin", upload.UploadId, 1, [1]);
        var last = await UploadPartAsync(bucket, "a.bin", upload.UploadId, 2, [2]);

        async Task<string> Fails(params PartETag[] parts) => (await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = "a.bin", UploadId = upload.UploadId, PartETags = [.. parts],
            }, _ct))).ErrorCode;

        // InvalidPartOrder is covered by unit tests: the SDK sorts PartETags by number before sending them.
        Assert.Equal("InvalidPart", await Fails(new PartETag(1, last.ETag)));
        Assert.Equal("InvalidPart", await Fails(new PartETag(3, last.ETag)));
        Assert.Equal("EntityTooSmall", await Fails(new PartETag(1, small.ETag), new PartETag(2, last.ETag)));
        var unknown = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = "a.bin", UploadId = Guid.CreateVersion7().ToString("N"), PartETags = [new PartETag(1, small.ETag)],
            }, _ct));
        Assert.Equal("NoSuchUpload", unknown.ErrorCode);
    }

    [Fact]
    public async Task Complete_WithCrc32_ReturnsTheCompositeChecksum()
    {
        var bucket = await NewBucketAsync();
        var upload = await InitiateAsync(bucket, "sum.bin", ChecksumAlgorithm.CRC32);
        var parts = new List<UploadPartResponse>();
        foreach (var (number, data) in new[] { (1, FivePlusMiB), (2, new byte[] { 7, 7, 7 }) })
        {
            parts.Add(await _s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = "sum.bin", UploadId = upload.UploadId, PartNumber = number,
                InputStream = new MemoryStream(data), ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
            }, _ct));
        }

        var completed = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "sum.bin", UploadId = upload.UploadId,
            PartETags = [.. parts.Select((p, i) => new PartETag { PartNumber = i + 1, ETag = p.ETag, ChecksumCRC32 = p.ChecksumCRC32 })],
        }, _ct);

        using var crc = new TimeStore.Cryptography.Checksums.ChecksumFactory().Create(TimeStore.Cryptography.Checksums.ChecksumAlgorithm.Crc32);
        foreach (var part in parts)
            crc.Append(Convert.FromBase64String(part.ChecksumCRC32));
        var expected = new byte[4];
        crc.GetHashAndReset(expected);
        Assert.Equal($"{Convert.ToBase64String(expected)}-2", completed.ChecksumCRC32);
        var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket, Key = "sum.bin", ChecksumMode = ChecksumMode.ENABLED,
        }, _ct);
        Assert.Equal(completed.ChecksumCRC32, head.ChecksumCRC32);
    }

    // --- Reading multipart objects (step 3d) -----------------------------------------------------------------------

    /// <summary>A completed three-part object (5 MiB, 5 MiB, 1234 bytes of random data) and its bytes.</summary>
    private async Task<(string Bucket, byte[][] Parts, byte[] All)> MultipartObjectAsync(string key)
    {
        var bucket = await NewBucketAsync();
        var random = new Random(key.Length);
        byte[][] parts = [new byte[5 * 1024 * 1024], new byte[5 * 1024 * 1024], new byte[1234]];
        foreach (var part in parts)
            random.NextBytes(part);

        var upload = await InitiateAsync(bucket, key);
        var etags = new List<PartETag>();
        for (var i = 0; i < parts.Length; i++)
            etags.Add(new PartETag(i + 1, (await UploadPartAsync(bucket, key, upload.UploadId, i + 1, parts[i])).ETag));
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = etags,
        }, _ct);
        return (bucket, parts, [.. parts.SelectMany(p => p)]);
    }

    private async Task<byte[]> ReadAsync(GetObjectRequest request)
    {
        using var response = await _s3.GetObjectAsync(request, _ct);
        var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy, _ct);
        return copy.ToArray();
    }

    [Fact]
    public async Task Get_ReturnsEveryByte_AndRangesAcrossPartBoundaries()
    {
        var (bucket, _, all) = await MultipartObjectAsync("whole.bin");

        Assert.Equal(all, await ReadAsync(new GetObjectRequest { BucketName = bucket, Key = "whole.bin" }));

        var boundary = 5 * 1024 * 1024;
        var across = await ReadAsync(new GetObjectRequest
        {
            BucketName = bucket, Key = "whole.bin", ByteRange = new ByteRange(boundary - 10, 2 * boundary + 9),
        });
        Assert.Equal(all.AsSpan(boundary - 10, boundary + 20).ToArray(), across);

        var tail = await ReadAsync(new GetObjectRequest { BucketName = bucket, Key = "whole.bin", ByteRange = new ByteRange("bytes=-2000") });
        Assert.Equal(all.AsSpan(all.Length - 2000).ToArray(), tail);
    }

    [Fact]
    public async Task PartNumber_SelectsOnePart_AndReportsThePartsCount()
    {
        var (bucket, parts, all) = await MultipartObjectAsync("parts.bin");

        using (var second = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "parts.bin", PartNumber = 2 }, _ct))
        {
            Assert.Equal(HttpStatusCode.PartialContent, second.HttpStatusCode);
            Assert.Equal(3, second.PartsCount);
            Assert.Equal($"bytes {parts[0].Length}-{parts[0].Length * 2 - 1}/{all.Length}", second.ContentRange);
            var copy = new MemoryStream();
            await second.ResponseStream.CopyToAsync(copy, _ct);
            Assert.Equal(parts[1], copy.ToArray());
        }

        var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "parts.bin", PartNumber = 3 }, _ct);
        Assert.Equal(parts[2].Length, head.ContentLength);
        Assert.Equal(3, head.PartsCount);

        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "parts.bin", PartNumber = 4 }, _ct));
        Assert.Equal("InvalidPartNumber", missing.ErrorCode);
    }

    [Fact]
    public async Task DeleteAndOverwrite_FreeEveryPartBlob()
    {
        var existing = BlobFiles();
        var (bucket, _, _) = await MultipartObjectAsync("gone.bin");
        var parts = BlobFiles().Except(existing).ToArray(); // this test's blobs (tests of a class run one at a time)

        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "gone.bin", ContentBody = "small now" }, _ct);
        Assert.Equal("small now", System.Text.Encoding.UTF8.GetString(await ReadAsync(new GetObjectRequest { BucketName = bucket, Key = "gone.bin" })));
        var single = BlobFiles().Except(existing).Except(parts).ToArray();
        await _s3.DeleteObjectAsync(bucket, "gone.bin", _ct);

        Assert.Equal(3, parts.Length);
        Assert.Single(single);
        string[] freed = [.. parts, .. single];
        await TimeStoreServer.WaitUntilAsync(() => !freed.Any(File.Exists), _ct); // freed after the deletion grace
        Assert.DoesNotContain(freed, File.Exists);
    }

    private HashSet<string> BlobFiles() =>
        [.. Directory.EnumerateFiles(Path.Combine(server.DataPath, "blobs"), "*", SearchOption.AllDirectories)];

    // --- FULL_OBJECT checksums (step 3e) ---------------------------------------------------------------------------

    [Fact]
    public async Task Complete_FullObjectCrc64Nvme_MatchesTheCrcOfAllBytes()
    {
        var bucket = await NewBucketAsync();
        var random = new Random(3);
        byte[][] parts = [new byte[5 * 1024 * 1024], new byte[4321]];
        foreach (var part in parts)
            random.NextBytes(part);
        var all = parts.SelectMany(p => p).ToArray();
        string Crc(byte[] data)
        {
            using var crc = new TimeStore.Cryptography.Checksums.ChecksumFactory().Create(TimeStore.Cryptography.Checksums.ChecksumAlgorithm.Crc64Nvme);
            crc.Append(data);
            var digest = new byte[8];
            crc.GetHashAndReset(digest);
            return Convert.ToBase64String(digest);
        }

        var upload = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = "full.bin", ChecksumAlgorithm = ChecksumAlgorithm.CRC64NVME, ChecksumType = ChecksumType.FULL_OBJECT,
        }, _ct);
        var etags = new List<PartETag>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = await _s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = "full.bin", UploadId = upload.UploadId, PartNumber = i + 1,
                InputStream = new MemoryStream(parts[i]), ChecksumAlgorithm = ChecksumAlgorithm.CRC64NVME,
            }, _ct);
            etags.Add(new PartETag { PartNumber = i + 1, ETag = part.ETag, ChecksumCRC64NVME = part.ChecksumCRC64NVME });
        }

        var completed = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "full.bin", UploadId = upload.UploadId, PartETags = etags,
            ChecksumCRC64NVME = Crc(all), ChecksumType = ChecksumType.FULL_OBJECT,
        }, _ct);

        Assert.Equal(Crc(all), completed.ChecksumCRC64NVME);
        Assert.Equal(ChecksumType.FULL_OBJECT, completed.ChecksumType);
        var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket, Key = "full.bin", ChecksumMode = ChecksumMode.ENABLED,
        }, _ct);
        Assert.Equal(Crc(all), head.ChecksumCRC64NVME);
        Assert.Equal(all, await ReadAsync(new GetObjectRequest { BucketName = bucket, Key = "full.bin", ChecksumMode = ChecksumMode.ENABLED }));
    }

    [Fact]
    public async Task Complete_WithAWrongFullObjectChecksum_IsBadDigest()
    {
        var bucket = await NewBucketAsync();
        var upload = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = "bad.bin", ChecksumAlgorithm = ChecksumAlgorithm.CRC32, ChecksumType = ChecksumType.FULL_OBJECT,
        }, _ct);
        var part = await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = "bad.bin", UploadId = upload.UploadId, PartNumber = 1,
            InputStream = new MemoryStream([1, 2, 3]), ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        }, _ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = "bad.bin", UploadId = upload.UploadId,
            PartETags = [new PartETag { PartNumber = 1, ETag = part.ETag, ChecksumCRC32 = part.ChecksumCRC32 }],
            ChecksumCRC32 = "AAAAAA==", ChecksumType = ChecksumType.FULL_OBJECT,
        }, _ct));

        Assert.Equal("BadDigest", error.ErrorCode);
    }

    [Fact]
    public async Task TransferUtility_UploadsAndDownloadsALargeFile_WithItsDefaults()
    {
        var bucket = await NewBucketAsync();
        var data = new byte[21 * 1024 * 1024]; // above the 16 MiB multipart threshold
        new Random(5).NextBytes(data);
        var transfer = new Amazon.S3.Transfer.TransferUtility(_s3);

        await transfer.UploadAsync(new MemoryStream(data), bucket, "large.bin", _ct);

        var head = await _s3.GetObjectMetadataAsync(bucket, "large.bin", _ct);
        Assert.Contains("-", head.ETag); // went through multipart
        Assert.Equal(data.Length, head.ContentLength);
        Assert.Equal(data, await ReadAsync(new GetObjectRequest { BucketName = bucket, Key = "large.bin" }));
    }
}
