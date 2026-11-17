using TimeStore.Contracts.Objects;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Objects;

/// <summary>
/// The admin UI's object browser (admin API). The same use cases as the S3 API (<see cref="IObjectService"/>,
/// <c>IUploadService</c>), with folders as a view over the <c>/</c> delimiter and responses as Contracts.
/// </summary>
public interface IObjectBrowserService
{
    /// <summary>Folders and objects directly in <paramref name="prefix"/>; the folder marker object itself is hidden.</summary>
    ValueTask<Result<ObjectListResponse>> ListAsync(
        string bucket, string prefix, string? startAfter, int limit, CancellationToken ct);

    /// <summary>Folders and all versions and delete markers directly in <paramref name="prefix"/>, newest first per key.</summary>
    ValueTask<Result<ObjectVersionListResponse>> ListVersionsAsync(
        string bucket, string prefix, string? keyMarker, string? versionIdMarker, int limit, CancellationToken ct);

    /// <summary>The latest version, or <paramref name="versionId"/>.</summary>
    ValueTask<Result<ObjectDetailsResponse>> DetailsAsync(string bucket, string key, string? versionId, CancellationToken ct);

    /// <summary>Opens the latest version or <paramref name="versionId"/> for a download (the caller disposes the reader).</summary>
    ValueTask<Result<ObjectReader>> OpenAsync(string bucket, string key, string? versionId, CancellationToken ct);

    /// <summary>Copies a version over its key: the restored content becomes the latest version.</summary>
    ValueTask<Result<ObjectWrittenResponse>> RestoreAsync(string bucket, RestoreVersionRequest request, CancellationToken ct);

    /// <summary>Stores <paramref name="body"/> (<paramref name="length"/> bytes) as one object.</summary>
    ValueTask<Result<ObjectWrittenResponse>> PutAsync(
        string bucket, string key, string? contentType, long? length, Stream body, CancellationToken ct);

    /// <summary>Deletes the keys, and every object under each prefix; returns how many objects were deleted.</summary>
    ValueTask<Result<DeleteObjectsResponse>> DeleteAsync(string bucket, DeleteObjectsRequest request, CancellationToken ct);

    ValueTask<Result<StartUploadResponse>> StartUploadAsync(string bucket, StartUploadRequest request, CancellationToken ct);

    ValueTask<Result<UploadedPartResponse>> UploadPartAsync(
        string bucket, string key, string uploadId, int partNumber, long? length, Stream body, CancellationToken ct);

    ValueTask<Result<ObjectWrittenResponse>> CompleteUploadAsync(
        string bucket, string key, string uploadId, CompleteUploadRequest request, CancellationToken ct);

    ValueTask<Result> AbortUploadAsync(string bucket, string key, string uploadId, CancellationToken ct);
}

public static class ObjectBrowserLimits
{
    /// <summary>The folder separator of the browser.</summary>
    public const string Delimiter = "/";

    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 1000;
}

public static class ObjectBrowserErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string InvalidPrefix = "OBJECT_BROWSER_INVALID_PREFIX";
        public const string NothingToDelete = "OBJECT_BROWSER_NOTHING_TO_DELETE";
    }

    public static readonly AppError InvalidPrefix = AppError.Validation(
        Codes.InvalidPrefix, $"A folder to delete must be a non-empty prefix ending with '{ObjectBrowserLimits.Delimiter}'.");

    public static readonly AppError NothingToDelete =
        AppError.Validation(Codes.NothingToDelete, "Name at least one object or folder to delete.");
}
