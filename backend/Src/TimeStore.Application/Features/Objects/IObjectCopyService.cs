using TimeStore.Domain.Objects;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Objects;

/// <param name="ReplaceMetadata">
/// <c>x-amz-metadata-directive: REPLACE</c>: content type, headers and user metadata come from the request; otherwise
/// (COPY, the default) from the source.
/// </param>
/// <param name="ChecksumAlgorithm">Upper-case algorithm to compute for the copy; null keeps the source's algorithm.</param>
/// <param name="OnlyIfAbsent"><c>If-None-Match: *</c> on the destination.</param>
public sealed record CopyObjectCommand(
    string Bucket,
    string Key,
    bool ReplaceMetadata,
    string? ContentType,
    IReadOnlyList<ObjectHeader> Headers,
    IReadOnlyList<ObjectHeader> UserMetadata,
    string? ChecksumAlgorithm,
    bool OnlyIfAbsent);

/// <param name="VersionId">The new object's <c>x-amz-version-id</c>, or null in a never-versioned bucket.</param>
public sealed record CopyObjectResult(string ETag, DateTime LastModified, ObjectChecksum? Checksum, string? VersionId = null);

/// <summary>CopyObject: a new object with the bytes of an existing one.</summary>
public interface IObjectCopyService
{
    /// <summary>
    /// Copies <paramref name="source"/> (opened, and its preconditions checked, by the caller: the bytes copied are
    /// exactly the version that was checked) to the destination. The bytes are copied into a new blob, never shared,
    /// so deleting either object never touches the other. The checksum is computed afresh while copying.
    /// </summary>
    ValueTask<Result<CopyObjectResult>> CopyAsync(CopyObjectCommand command, ObjectReader source, CancellationToken ct);
}
