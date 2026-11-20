using TimeStore.Infra.Services.Objects;
using TimeStore.Application.Abstractions;
using System.Security.Cryptography;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Domain.Uploads;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Uploads;

public sealed class UploadService(
    IMetadataStore store, IBlobStore blobs, IBlobDeletions deletions, IChecksumFactory checksums, TimeProvider clock)
    : IUploadService
{
    private const int Md5Length = 16;

    /// <summary>A part re-uploaded between reading and committing makes the commit refuse; re-read this many times.</summary>
    private const int MaxCompleteAttempts = 3;

    public async ValueTask<Result<string>> CreateAsync(CreateUploadCommand command, CancellationToken ct)
    {
        if (UploadValidation.ValidateCreate(command) is { } invalid)
            return invalid;
        if (await store.FindBucketAsync(command.Bucket, ct) is not { } bucket)
            return BucketErrors.NotFound(command.Bucket);

        var upload = new MultipartUpload(
            Guid.CreateVersion7(), bucket.Id, command.Key, command.ContentType ?? StoredObject.DefaultContentType,
            command.ChecksumAlgorithm, command.ChecksumType, command.Headers, command.UserMetadata, clock.UtcNowMs);
        return await store.TryCreateUploadAsync(upload, ct)
            ? UploadIds.ToText(upload.UploadId)
            : BucketErrors.NotFound(command.Bucket);
    }

    public async ValueTask<Result<PartUploaded>> UploadPartAsync(
        UploadPartCommand command, Stream body, Func<ObjectChecksum?> verifiedChecksum, CancellationToken ct)
    {
        if (UploadValidation.ValidatePart(command) is { } invalid)
            return invalid;
        var found = await FindUploadAsync(command.Bucket, command.Key, command.UploadId, ct);
        if (found.IsFailure)
            return found.Error;
        var upload = found.Value;

        // Throws on a body that breaks its integrity promise; the blob store removes its temporary file then.
        var blob = await blobs.WriteAsync(body, ct);
        var committed = false;
        try
        {
            if (command.ContentMd5 is { } md5 && !Convert.FromHexString(blob.Md5Hex).AsSpan().SequenceEqual(md5))
                return ObjectErrors.BadDigest;

            // An upload with an algorithm needs that checksum on every part (the object checksum is built from them).
            // Without one, a part's checksum is still verified and kept for ListParts.
            var checksum = verifiedChecksum();
            if (upload.ChecksumAlgorithm is { } expected && checksum?.Algorithm != expected)
                return UploadErrors.ChecksumMismatch(expected, checksum?.Algorithm);

            var part = new UploadPart(upload.UploadId, command.PartNumber, blob, checksum?.Digest, clock.UtcNowMs);
            var commit = await store.CommitPartAsync(part, ct);
            if (commit.Status == PartCommitStatus.UploadMissing)
                return UploadErrors.NotFound; // completed or aborted while this part was uploading

            committed = true;
            if (commit.Replaced is not null)
                await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
            return new PartUploaded(part.ETag, checksum);
        }
        finally
        {
            if (!committed)
                await blobs.DeleteQuietlyAsync(blob);
        }
    }

    public async ValueTask<Result<PartCopied>> UploadPartCopyAsync(
        UploadPartCopyCommand command, ObjectReader source, CancellationToken ct)
    {
        var size = source.Object.Size;
        if (command.Offset < 0 || command.Length < 0 || command.Offset + command.Length > size || (command.Length == 0 && size > 0))
            return UploadErrors.CopyRangeOutOfBounds(size);
        if (command.Length > StoredObject.SinglePutMaxBytes)
            return ObjectErrors.CopySourceTooLarge;
        var found = await FindUploadAsync(command.Bucket, command.Key, command.UploadId, ct);
        if (found.IsFailure)
            return found.Error;

        source.Content.Seek(command.Offset, SeekOrigin.Begin);
        await using var slice = new SliceReadStream(source.Content, command.Length);

        // The upload's algorithm is computed while copying: the part then carries it like an uploaded part.
        var algorithmName = found.Value.ChecksumAlgorithm;
        using var checksum = algorithmName is not null && ChecksumAlgorithms.TryParse(algorithmName, out var algorithm)
            ? checksums.Create(algorithm)
            : null;
        Stream body = checksum is null ? slice : new ChecksummingReadStream(slice, checksum);

        var part = new UploadPartCommand(command.Bucket, command.Key, command.UploadId, command.PartNumber, command.Length, ContentMd5: null);
        var uploaded = await UploadPartAsync(part, body, () =>
        {
            if (checksum is null)
                return null;
            var digest = new byte[ChecksumAlgorithms.LengthOf(checksum.Algorithm)];
            checksum.GetHashAndReset(digest);
            return new ObjectChecksum(algorithmName!, digest);
        }, ct);
        return uploaded.IsFailure
            ? uploaded.Error
            : new PartCopied(uploaded.Value.ETag, uploaded.Value.Checksum, clock.UtcNowMs);
    }

    public async ValueTask<Result<UploadCompleted>> CompleteAsync(CompleteUploadCommand command, CancellationToken ct)
    {
        if (ValidateOrder(command.Parts) is { } invalid)
            return invalid;
        command = command with { Parts = LastOfEachNumber(command.Parts) };
        if (await store.FindBucketAsync(command.Bucket, ct) is not { } bucket)
            return BucketErrors.NotFound(command.Bucket);
        if (!UploadIds.TryParse(command.UploadId, out var uploadId))
            return UploadErrors.NotFound;

        for (var attempt = 1; ; attempt++)
        {
            var upload = await store.FindUploadAsync(bucket.Id, command.Key, uploadId, ct);
            if (upload is null)
                return await CompletedEarlierAsync(bucket.Id, command.Key, uploadId, ct) ?? (Result<UploadCompleted>)UploadErrors.NotFound;

            var built = await BuildObjectAsync(upload, command.Parts, command.ExpectedChecksum, ct);
            if (built.IsFailure)
                return built.Error;
            var (stored, completed) = built.Value;

            var completion = await store.CompleteUploadAsync(
                stored, command.OnlyIfAbsent ? CommitCondition.IfAbsent : CommitCondition.Always, ct);
            switch (completion.Status)
            {
                case CompletionStatus.Completed:
                    await deletions.ReleasedAsync(); // replaced data and unlisted parts: recorded by the commit
                    return completed with { VersionId = completion.Version?.WriteHeader };
                case CompletionStatus.BucketMissing:
                    return BucketErrors.NotFound(command.Bucket);
                case CompletionStatus.ConditionFailed:
                    return ObjectErrors.AlreadyExists(command.Key);
                case CompletionStatus.UploadMissing: // a concurrent completion or abort won
                    return await CompletedEarlierAsync(bucket.Id, command.Key, uploadId, ct) ?? (Result<UploadCompleted>)UploadErrors.NotFound;
                case CompletionStatus.PartsChanged when attempt < MaxCompleteAttempts:
                    continue;
                case CompletionStatus.PartsChanged:
                    return UploadErrors.InvalidPart(command.Parts[0].PartNumber);
            }
        }
    }

    /// <summary>
    /// A number listed more than once (a client that re-uploaded a part and recorded both answers) counts once, with
    /// its last entry: the one that must match the stored part.
    /// </summary>
    private static List<CompletedPart> LastOfEachNumber(IReadOnlyList<CompletedPart> parts)
    {
        var result = new List<CompletedPart>(parts.Count);
        foreach (var part in parts)
        {
            if (result.Count > 0 && result[^1].PartNumber == part.PartNumber)
                result[^1] = part;
            else
                result.Add(part);
        }

        return result;
    }

    /// <summary>Part numbers in range and in non-decreasing order (repeats allowed, see <see cref="LastOfEachNumber"/>).</summary>
    private static AppError? ValidateOrder(IReadOnlyList<CompletedPart> parts)
    {
        if (parts.Count == 0)
            return UploadErrors.NoParts;
        var previous = 0;
        foreach (var part in parts)
        {
            if (part.PartNumber is < MultipartUpload.MinPartNumber or > MultipartUpload.MaxPartNumber)
                return UploadErrors.InvalidPartNumber;
            if (part.PartNumber < previous)
                return UploadErrors.InvalidPartOrder;
            previous = part.PartNumber;
        }

        return null;
    }

    /// <summary>
    /// Matches the listed parts against the stored ones and builds the object: manifest with offsets, <c>md5-N</c> ETag,
    /// composite checksum. Pure apart from reading the parts.
    /// </summary>
    private async ValueTask<Result<(StoredObject Stored, UploadCompleted Completed)>> BuildObjectAsync(
        MultipartUpload upload, IReadOnlyList<CompletedPart> listed, ObjectChecksum? expected, CancellationToken ct)
    {
        var page = await store.ListPartsAsync(upload.UploadId, afterPartNumber: 0, MultipartUpload.MaxPartNumber, ct);
        var stored = page.Parts.ToDictionary(p => p.PartNumber);

        var manifest = new ObjectPart[listed.Count];
        var md5s = new byte[listed.Count * Md5Length];
        var offset = 0L;
        for (var i = 0; i < listed.Count; i++)
        {
            var request = listed[i];
            if (!stored.TryGetValue(request.PartNumber, out var part)
                || !string.Equals(request.ETag, part.ETag, StringComparison.OrdinalIgnoreCase)
                || (request.Checksum is { } claimed && !ClaimMatches(upload, part, claimed)))
                return UploadErrors.InvalidPart(request.PartNumber);

            // Every part but the last must reach the minimum (checked here, not at upload: the last is only known now).
            if (i < listed.Count - 1 && part.Blob.Size < MultipartUpload.MinPartSize)
                return UploadErrors.PartTooSmall(request.PartNumber);

            manifest[i] = new ObjectPart(part.PartNumber, offset, part.Blob, part.Checksum);
            Convert.FromHexString(part.ETag, md5s.AsSpan(i * Md5Length, Md5Length), out _, out _);
            offset += part.Blob.Size;
        }

        var etag = $"{Convert.ToHexStringLower(MD5.HashData(md5s))}-{listed.Count}";
        ObjectChecksum? checksum = null;
        if (upload.ChecksumAlgorithm is { } algorithmName)
        {
            checksum = upload.ChecksumType == ChecksumTypes.FullObject
                ? FullObject(algorithmName, manifest)
                : Composite(algorithmName, manifest);
        }

        if (expected is not null)
        {
            if (expected.Algorithm != checksum?.Algorithm)
                return UploadErrors.ChecksumMismatch(upload.ChecksumAlgorithm, expected.Algorithm);
            if (!expected.Digest.AsSpan().SequenceEqual(checksum.Digest))
                return UploadErrors.BadObjectChecksum;
        }

        var obj = new StoredObject(
            upload.BucketId, upload.Key, Guid.CreateVersion7(), new MultipartBlobs(upload.UploadId, manifest), etag,
            upload.ContentType, checksum?.Algorithm, checksum?.Digest, checksum is null ? null : upload.ChecksumType,
            upload.Headers, upload.UserMetadata, clock.UtcNowMs);
        return (obj, new UploadCompleted(etag, checksum, obj.ChecksumType, listed.Count));
    }

    /// <summary>
    /// A part checksum listed in the completion must be the stored one. With an upload algorithm it must also be that
    /// algorithm; without one, a part may still carry a checksum (SDKs send CRC32 by default) and is compared by digest.
    /// </summary>
    private static bool ClaimMatches(MultipartUpload upload, UploadPart part, ObjectChecksum claimed)
    {
        if (upload.ChecksumAlgorithm is { } expected && claimed.Algorithm != expected)
            return false;
        return part.Checksum is null ? upload.ChecksumAlgorithm is null : claimed.Digest.AsSpan().SequenceEqual(part.Checksum);
    }

    /// <summary>The checksum of the concatenated part checksums (Amazon S3 COMPOSITE; shown as <c>base64-N</c>).</summary>
    private ObjectChecksum Composite(string algorithmName, IReadOnlyList<ObjectPart> parts)
    {
        if (!ChecksumAlgorithms.TryParse(algorithmName, out var algorithm))
            throw new InvalidDataException($"Upload checksum algorithm '{algorithmName}' is unknown.");
        using var checksum = checksums.Create(algorithm);
        foreach (var part in parts)
            checksum.Append(part.Checksum ?? throw new InvalidDataException($"Part {part.PartNumber} has no checksum."));
        var digest = new byte[checksum.Length];
        checksum.GetHashAndReset(digest);
        return new ObjectChecksum(algorithmName, digest);
    }

    /// <summary>
    /// The CRC of the whole object (Amazon S3 FULL_OBJECT), combined from the part CRCs and sizes: no data is read.
    /// </summary>
    private static ObjectChecksum FullObject(string algorithmName, IReadOnlyList<ObjectPart> parts)
    {
        if (!ChecksumAlgorithms.TryParse(algorithmName, out var algorithm) || !CrcCombine.Supports(algorithm))
            throw new InvalidDataException($"Upload checksum algorithm '{algorithmName}' cannot be combined.");
        var running = new byte[ChecksumAlgorithms.LengthOf(algorithm)]; // the CRC of no data is zero
        foreach (var part in parts)
        {
            var digest = part.Checksum ?? throw new InvalidDataException($"Part {part.PartNumber} has no checksum.");
            CrcCombine.Combine(algorithm, running, digest, part.Blob.Size, running);
        }

        return new ObjectChecksum(algorithmName, running);
    }

    /// <summary>
    /// A retried completion: the upload is gone, but the current object came from it. Amazon S3 answers the retry
    /// with success, so clients that lost the first response do not fail.
    /// </summary>
    private async ValueTask<Result<UploadCompleted>?> CompletedEarlierAsync(
        Guid bucketId, string key, Guid uploadId, CancellationToken ct)
    {
        if (await store.FindObjectAsync(bucketId, key, ct) is not { Data: MultipartBlobs data } current || data.UploadId != uploadId)
            return null;
        var checksum = current.ChecksumAlgorithm is { } algorithm && current.Checksum is { } digest
            ? new ObjectChecksum(algorithm, digest)
            : null;
        return new UploadCompleted(current.ETag, checksum, current.ChecksumType, data.Parts.Count);
    }

    public async ValueTask<Result> AbortAsync(string bucket, string key, string uploadId, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);
        if (!UploadIds.TryParse(uploadId, out var id)
            || await store.AbortUploadAsync(found.Id, key, id, ct) is not { } parts)
            return UploadErrors.NotFound;

        await deletions.ReleasedAsync(); // recorded by the commit; collected after the wait
        return Result.Success;
    }

    public async ValueTask<Result<PartListing>> ListPartsAsync(
        string bucket, string key, string uploadId, int partNumberMarker, int maxParts, CancellationToken ct)
    {
        var found = await FindUploadAsync(bucket, key, uploadId, ct);
        if (found.IsFailure)
            return found.Error;
        var page = await store.ListPartsAsync(found.Value.UploadId, partNumberMarker, maxParts, ct);
        return new PartListing(found.Value, page);
    }

    public async ValueTask<Result<UploadPage>> ListUploadsAsync(
        string bucket, string prefix, string? delimiter, string? keyMarker, string? uploadIdMarker, int maxUploads,
        CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not { } found)
            return BucketErrors.NotFound(bucket);

        // Amazon S3 ignores upload-id-marker without key-marker.
        Guid? marker = null;
        if (keyMarker is not null && !string.IsNullOrEmpty(uploadIdMarker))
        {
            if (!UploadIds.TryParse(uploadIdMarker, out var parsed))
                return UploadErrors.InvalidMarker;
            marker = parsed;
        }

        return await store.ListUploadsAsync(found.Id, prefix, delimiter, keyMarker, marker, maxUploads, ct);
    }

    private async ValueTask<Result<MultipartUpload>> FindUploadAsync(
        string bucket, string key, string uploadId, CancellationToken ct)
    {
        if (await store.FindBucketAsync(bucket, ct) is not Bucket found)
            return BucketErrors.NotFound(bucket);
        return UploadIds.TryParse(uploadId, out var id) && await store.FindUploadAsync(found.Id, key, id, ct) is { } upload
            ? upload
            : UploadErrors.NotFound;
    }
}
