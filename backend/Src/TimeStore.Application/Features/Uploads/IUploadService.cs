using TimeStore.Application.Features.Objects;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Uploads;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Uploads;

/// <param name="ChecksumAlgorithm">Upper-case algorithm every part must use, or null.</param>
/// <param name="ChecksumType"><c>ChecksumTypes</c> value when an algorithm is set (defaults resolved by the caller).</param>
public sealed record CreateUploadCommand(
    string Bucket,
    string Key,
    string? ContentType,
    string? ChecksumAlgorithm,
    string? ChecksumType,
    IReadOnlyList<ObjectHeader> Headers,
    IReadOnlyList<ObjectHeader> UserMetadata);

/// <param name="UploadId">As the client sent it (<see cref="UploadIds"/> format).</param>
/// <param name="Length">Payload size from the request; required.</param>
/// <param name="ContentMd5">Decoded Content-MD5 (16 bytes) or null.</param>
public sealed record UploadPartCommand(
    string Bucket, string Key, string UploadId, int PartNumber, long? Length, byte[]? ContentMd5);

public sealed record PartUploaded(string ETag, ObjectChecksum? Checksum);

/// <summary>UploadPartCopy: <paramref name="Length"/> bytes of the source from <paramref name="Offset"/> become the part.</summary>
public sealed record UploadPartCopyCommand(
    string Bucket, string Key, string UploadId, int PartNumber, long Offset, long Length);

/// <param name="LastModified">When the part was stored (the <c>CopyPartResult</c> time).</param>
public sealed record PartCopied(string ETag, ObjectChecksum? Checksum, DateTime LastModified);

/// <summary>A part as the client lists it in CompleteMultipartUpload.</summary>
/// <param name="ETag">Without quotes.</param>
/// <param name="Checksum">The part checksum the client claims (its algorithm and digest), or null.</param>
public sealed record CompletedPart(int PartNumber, string ETag, ObjectChecksum? Checksum);

/// <param name="Parts">In request order (the order rules are checked here).</param>
/// <param name="OnlyIfAbsent"><c>If-None-Match: *</c>: fail when the key already exists.</param>
/// <param name="ExpectedChecksum">
/// The object checksum the client sent (<c>x-amz-checksum-*</c> header; for composite without the <c>-N</c>), or null.
/// It must equal the checksum the completion computes.
/// </param>
public sealed record CompleteUploadCommand(
    string Bucket, string Key, string UploadId, IReadOnlyList<CompletedPart> Parts, bool OnlyIfAbsent,
    ObjectChecksum? ExpectedChecksum = null);

/// <param name="ETag"><c>md5-N</c>, without quotes.</param>
/// <param name="Checksum">The object checksum (composite: over the part checksums; full-object: the CRC of all bytes), or null.</param>
/// <param name="ChecksumType"><c>ChecksumTypes</c> value when there is a checksum.</param>
/// <param name="VersionId">The object's <c>x-amz-version-id</c>, or null in a never-versioned bucket.</param>
public sealed record UploadCompleted(string ETag, ObjectChecksum? Checksum, string? ChecksumType, int PartsCount, string? VersionId = null);

/// <summary>A ListParts page with the upload it belongs to (its checksum algorithm and type).</summary>
public sealed record PartListing(MultipartUpload Upload, PartPage Page);

/// <summary>Multipart upload use cases.</summary>
public interface IUploadService
{
    /// <summary>Starts an upload; returns its id (<see cref="UploadIds"/> format).</summary>
    ValueTask<Result<string>> CreateAsync(CreateUploadCommand command, CancellationToken ct);

    /// <summary>
    /// Stores one part: validates, writes the blob, checks Content-MD5 and the upload's checksum algorithm, commits.
    /// Re-uploading a part number replaces the part. <paramref name="verifiedChecksum"/> is read after the body.
    /// </summary>
    ValueTask<Result<PartUploaded>> UploadPartAsync(
        UploadPartCommand command, Stream body, Func<ObjectChecksum?> verifiedChecksum, CancellationToken ct);

    /// <summary>
    /// Commits the listed parts as the object (a manifest: no data is copied). Checks order, existence, ETags, part
    /// checksums and minimum sizes; builds the <c>md5-N</c> ETag and the object checksum (composite, or full-object
    /// combined from the part CRCs). A retry of a completion
    /// that already succeeded returns the stored result. Unlisted parts and the replaced object are freed afterwards.
    /// </summary>
    ValueTask<Result<UploadCompleted>> CompleteAsync(CompleteUploadCommand command, CancellationToken ct);

    /// <summary>
    /// UploadPartCopy: stores a slice of <paramref name="source"/> as a part through the same path as
    /// <see cref="UploadPartAsync"/> (the upload's checksum algorithm is computed while copying). The range must lie
    /// inside the source (checked here) and be at most 5 GiB.
    /// </summary>
    ValueTask<Result<PartCopied>> UploadPartCopyAsync(UploadPartCopyCommand command, ObjectReader source, CancellationToken ct);

    /// <summary>Removes the upload and its parts; <c>NoSuchUpload</c> when it is unknown.</summary>
    ValueTask<Result> AbortAsync(string bucket, string key, string uploadId, CancellationToken ct);

    ValueTask<Result<PartListing>> ListPartsAsync(
        string bucket, string key, string uploadId, int partNumberMarker, int maxParts, CancellationToken ct);

    ValueTask<Result<UploadPage>> ListUploadsAsync(
        string bucket, string prefix, string? delimiter, string? keyMarker, string? uploadIdMarker, int maxUploads,
        CancellationToken ct);
}
