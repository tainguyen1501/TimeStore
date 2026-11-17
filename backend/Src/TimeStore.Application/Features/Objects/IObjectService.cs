using TimeStore.Domain.Objects;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Objects;

/// <param name="Length">Payload size from the request (Content-Length or x-amz-decoded-content-length); required.</param>
/// <param name="ContentMd5">Decoded Content-MD5 (16 bytes) or null.</param>
/// <param name="Headers">System headers to store (Cache-Control, Content-Disposition, ...), lower-case names.</param>
/// <param name="UserMetadata">x-amz-meta-* without the prefix, lower-case names.</param>
/// <param name="OnlyIfAbsent"><c>If-None-Match: *</c>: fail when the key already exists.</param>
public sealed record PutObjectCommand(
    string Bucket,
    string Key,
    long? Length,
    string? ContentType,
    byte[]? ContentMd5,
    IReadOnlyList<ObjectHeader> Headers,
    IReadOnlyList<ObjectHeader> UserMetadata,
    bool OnlyIfAbsent);

/// <param name="VersionId">The <c>x-amz-version-id</c> to send, or null in a never-versioned bucket.</param>
public sealed record PutObjectResult(string ETag, ObjectChecksum? Checksum, string? VersionId = null);

/// <summary>A delete marker met by a read: the latest version (404 NoSuchKey) or the version asked for (405).</summary>
public sealed record DeleteMarkerHit(string VersionId, DateTime LastModified, bool Named);

/// <summary>
/// A read by version: the object with its <c>x-amz-version-id</c> (null in a never-versioned bucket), or the delete
/// marker it hit (<see cref="Object"/> is then null).
/// </summary>
public sealed record ObjectVersionHead(StoredObject? Object, string? VersionId, DeleteMarkerHit? Marker);

/// <summary>As <see cref="ObjectVersionHead"/> with the content open; dispose it to close the stream.</summary>
public sealed record ObjectVersionOpen(ObjectReader? Reader, string? VersionId, DeleteMarkerHit? Marker) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Reader?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>One key of a delete: <paramref name="VersionId"/> as the client sent it, or null for the latest.</summary>
public sealed record DeleteObjectEntry(string Key, string? VersionId);

/// <summary>
/// What a delete did, for the response: <c>x-amz-version-id</c> (the version removed, or the marker created) and
/// whether a delete marker was created or removed. <paramref name="Error"/>: this key failed (multi-object delete).
/// </summary>
public sealed record ObjectDeleted(string Key, string? VersionId, bool DeleteMarker, AppError? Error = null);

/// <summary>An object opened for reading. Dispose it to release the content stream.</summary>
public sealed record ObjectReader(StoredObject Object, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Object use cases shared by the S3 API (and the admin UI later).</summary>
public interface IObjectService
{
    /// <summary>
    /// Stores <paramref name="body"/> as the object: validates first, writes the blob (durable), checks Content-MD5,
    /// commits the metadata (the commit point) and deletes the replaced blob. <paramref name="verifiedChecksum"/> is
    /// read after the body has been consumed (trailer checksums are only known then).
    /// </summary>
    ValueTask<Result<PutObjectResult>> PutAsync(
        PutObjectCommand command, Stream body, Func<ObjectChecksum?> verifiedChecksum, CancellationToken ct);

    /// <summary>
    /// The object's metadata (HEAD), or NoSuchBucket / NoSuchKey. Returns the Domain record on purpose: GET/HEAD are
    /// the hottest path and the S3 handler maps it straight to headers.
    /// </summary>
    ValueTask<Result<StoredObject>> HeadAsync(string bucket, string key, CancellationToken ct);

    /// <summary>
    /// Metadata plus an open content stream (seekable for ranges). The stream is opened before returning. Multipart
    /// objects open their first part up front and later parts as the read reaches them; an overwrite or delete does
    /// not break such a read because released blobs are deleted only after a grace (<c>IBlobDeletions</c>,
    /// <c>TimeStore:Blobs:DeleteDelaySeconds</c>).
    /// </summary>
    ValueTask<Result<ObjectReader>> OpenAsync(string bucket, string key, CancellationToken ct);

    /// <summary>
    /// One page of a listing in UTF-8 key order (see <c>IMetadataStore.ListObjectsAsync</c>), or NoSuchBucket.
    /// Returns the Domain page: listings are hot and map straight to XML.
    /// </summary>
    ValueTask<Result<ObjectPage>> ListAsync(
        string bucket, string prefix, string? delimiter, string? startAfter, int maxEntries, CancellationToken ct);

    /// <summary>
    /// Deletes the latest (a delete marker in a versioned bucket) or, with <paramref name="versionId"/>, exactly that
    /// version. Succeeds when nothing matches (idempotent, as Amazon S3). Released blobs go after the commit.
    /// </summary>
    ValueTask<Result<ObjectDeleted>> DeleteAsync(string bucket, string key, string? versionId, CancellationToken ct);

    /// <summary>
    /// Multi-object delete in one metadata transaction; every valid key counts as deleted (missing ones too), a malformed
    /// version id fails its key only.
    /// </summary>
    ValueTask<Result<IReadOnlyList<ObjectDeleted>>> DeleteManyAsync(
        string bucket, IReadOnlyList<DeleteObjectEntry> entries, CancellationToken ct);

    /// <summary>
    /// The latest version, or the version named by <paramref name="versionId"/>; a delete marker is reported, not an
    /// error. <c>OBJECT_NOT_FOUND</c>, <c>OBJECT_NO_SUCH_VERSION</c>, <c>OBJECT_INVALID_VERSION_ID</c>.
    /// </summary>
    ValueTask<Result<ObjectVersionHead>> HeadVersionAsync(string bucket, string key, string? versionId, CancellationToken ct);

    /// <summary>As <see cref="HeadVersionAsync"/> with the content open (seekable for ranges).</summary>
    ValueTask<Result<ObjectVersionOpen>> OpenVersionAsync(string bucket, string key, string? versionId, CancellationToken ct);

    /// <summary>
    /// ListObjectVersions: versions and delete markers in key order, newest first; <paramref name="versionIdMarker"/>
    /// as the client sent it.
    /// </summary>
    ValueTask<Result<VersionPage>> ListVersionsAsync(
        string bucket, string prefix, string? delimiter, string? keyMarker, string? versionIdMarker, int maxEntries,
        CancellationToken ct);
}
