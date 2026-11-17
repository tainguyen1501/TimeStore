using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Contracts.Objects;
using TimeStore.Domain.Objects;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Objects;

/// <summary>The admin UI's view of objects: delegates to the S3 use cases and maps to Contracts.</summary>
public sealed class ObjectBrowserService(IObjectService objects, IUploadService uploads, IObjectCopyService copies)
    : IObjectBrowserService
{
    private static readonly ObjectHeader[] NoHeaders = [];

    public async ValueTask<Result<ObjectListResponse>> ListAsync(
        string bucket, string prefix, string? startAfter, int limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit, 1, ObjectBrowserLimits.MaxPageSize);
        var page = await objects.ListAsync(bucket, prefix, ObjectBrowserLimits.Delimiter, startAfter, pageSize, ct);
        if (page.IsFailure)
            return page.Error;

        var items = new List<ObjectItemResponse>(page.Value.Objects.Count);
        foreach (var entry in page.Value.Objects)
        {
            if (entry.Key == prefix)
                continue; // the folder's own marker object ("photos/"), shown as the folder itself
            items.Add(new ObjectItemResponse(entry.Key, entry.Size, entry.ETag, entry.CreatedAt, VersionId: null));
        }

        var folders = page.Value.CommonPrefixes.Select(static p => new FolderResponse(p)).ToArray();
        return new ObjectListResponse(
            prefix, folders, items, page.Value.IsTruncated, page.Value.IsTruncated ? page.Value.LastEntry : null);
    }

    public async ValueTask<Result<ObjectVersionListResponse>> ListVersionsAsync(
        string bucket, string prefix, string? keyMarker, string? versionIdMarker, int limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit, 1, ObjectBrowserLimits.MaxPageSize);
        var page = await objects.ListVersionsAsync(bucket, prefix, ObjectBrowserLimits.Delimiter, keyMarker, versionIdMarker, pageSize, ct);
        if (page.IsFailure)
            return page.Error;

        var versions = page.Value.Versions
            .Where(v => v.Key != prefix) // the folder's own marker object
            .Select(static v => new ObjectVersionItemResponse(v.Key, v.VersionIdText, v.IsLatest, v.IsDeleteMarker, v.Size, v.ETag, v.CreatedAt))
            .ToArray();
        return new ObjectVersionListResponse(
            prefix, page.Value.CommonPrefixes.Select(static p => new FolderResponse(p)).ToArray(), versions,
            page.Value.IsTruncated, page.Value.NextKeyMarker, page.Value.NextVersionIdMarker);
    }

    public async ValueTask<Result<ObjectDetailsResponse>> DetailsAsync(
        string bucket, string key, string? versionId, CancellationToken ct)
    {
        var head = await objects.HeadVersionAsync(bucket, key, versionId, ct);
        if (head.IsFailure)
            return head.Error;
        if (head.Value.Object is not { } stored)
            return versionId is null ? ObjectErrors.NotFound(key) : ObjectErrors.NoSuchVersion; // a delete marker

        return new ObjectDetailsResponse(
            stored.Key,
            stored.Data.Size,
            stored.ETag,
            stored.ContentType,
            stored.CreatedAt,
            head.Value.VersionId,
            stored.ChecksumAlgorithm,
            stored.Checksum is { } digest ? Convert.ToBase64String(digest) : null,
            stored.ChecksumType,
            stored.Data is MultipartBlobs multipart ? multipart.Parts.Count : null,
            stored.Headers.Select(static h => new ObjectHeaderResponse(h.Name, h.Value)).ToArray(),
            stored.UserMetadata.Select(static h => new ObjectHeaderResponse(h.Name, h.Value)).ToArray());
    }

    public async ValueTask<Result<ObjectReader>> OpenAsync(string bucket, string key, string? versionId, CancellationToken ct)
    {
        var opened = await objects.OpenVersionAsync(bucket, key, versionId, ct);
        if (opened.IsFailure)
            return opened.Error;
        if (opened.Value.Reader is { } reader)
            return reader;
        return versionId is null ? ObjectErrors.NotFound(key) : ObjectErrors.NoSuchVersion;
    }

    public async ValueTask<Result<ObjectWrittenResponse>> RestoreAsync(
        string bucket, RestoreVersionRequest request, CancellationToken ct)
    {
        var opened = await OpenAsync(bucket, request.Key, request.VersionId, ct);
        if (opened.IsFailure)
            return opened.Error;
        await using var reader = opened.Value;
        var command = new CopyObjectCommand(
            bucket, request.Key, ReplaceMetadata: false, ContentType: null, NoHeaders, NoHeaders, ChecksumAlgorithm: null,
            OnlyIfAbsent: false);
        var copied = await copies.CopyAsync(command, reader, ct);
        return copied.IsFailure ? copied.Error : new ObjectWrittenResponse(request.Key, copied.Value.ETag, copied.Value.VersionId);
    }

    public async ValueTask<Result<ObjectWrittenResponse>> PutAsync(
        string bucket, string key, string? contentType, long? length, Stream body, CancellationToken ct)
    {
        var command = new PutObjectCommand(bucket, key, length, contentType, ContentMd5: null, NoHeaders, NoHeaders, OnlyIfAbsent: false);
        var put = await objects.PutAsync(command, body, static () => null, ct);
        return put.IsFailure ? put.Error : new ObjectWrittenResponse(key, put.Value.ETag, put.Value.VersionId);
    }

    public async ValueTask<Result<DeleteObjectsResponse>> DeleteAsync(
        string bucket, DeleteObjectsRequest request, CancellationToken ct)
    {
        var keys = request.Keys ?? [];
        var prefixes = request.Prefixes ?? [];
        var versions = request.Versions ?? [];
        if (keys.Count == 0 && prefixes.Count == 0 && versions.Count == 0)
            return ObjectBrowserErrors.NothingToDelete;
        if (keys.Count + versions.Count > ObjectErrors.MaxDeleteKeys)
            return ObjectErrors.TooManyKeys;
        foreach (var prefix in prefixes)
        {
            if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith(ObjectBrowserLimits.Delimiter, StringComparison.Ordinal))
                return ObjectBrowserErrors.InvalidPrefix;
        }

        var deleted = 0;
        if (keys.Count > 0)
        {
            var result = await objects.DeleteManyAsync(bucket, Latest(keys), ct);
            if (result.IsFailure)
                return result.Error;
            deleted += keys.Distinct(StringComparer.Ordinal).Count();
        }

        if (versions.Count > 0)
        {
            var result = await objects.DeleteManyAsync(
                bucket, versions.Select(static v => new DeleteObjectEntry(v.Key, v.VersionId)).ToArray(), ct);
            if (result.IsFailure)
                return result.Error;
            if (result.Value.FirstOrDefault(static r => r.Error is not null)?.Error is { } failed)
                return failed;
            deleted += versions.Count;
        }

        foreach (var prefix in prefixes)
        {
            var removed = await DeletePrefixAsync(bucket, prefix, ct);
            if (removed.IsFailure)
                return removed.Error;
            deleted += removed.Value;
        }

        return new DeleteObjectsResponse(deleted);
    }

    /// <summary>Every object under the prefix, one page (one metadata transaction) at a time.</summary>
    private async ValueTask<Result<int>> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct)
    {
        var deleted = 0;
        string? startAfter = null;
        while (true)
        {
            var page = await objects.ListAsync(bucket, prefix, delimiter: null, startAfter, ObjectErrors.MaxDeleteKeys, ct);
            if (page.IsFailure)
                return page.Error;
            if (page.Value.Objects.Count == 0)
                return deleted;

            var keys = page.Value.Objects.Select(static o => o.Key).ToArray();
            var result = await objects.DeleteManyAsync(bucket, Latest(keys), ct);
            if (result.IsFailure)
                return result.Error;
            deleted += keys.Length;
            if (!page.Value.IsTruncated)
                return deleted;
            startAfter = keys[^1];
        }
    }

    /// <summary>Deletes of the latest version: in a versioned bucket each adds a delete marker (as the S3 console).</summary>
    private static DeleteObjectEntry[] Latest(IReadOnlyList<string> keys) =>
        keys.Select(static k => new DeleteObjectEntry(k, VersionId: null)).ToArray();

    public async ValueTask<Result<StartUploadResponse>> StartUploadAsync(
        string bucket, StartUploadRequest request, CancellationToken ct)
    {
        var command = new CreateUploadCommand(bucket, request.Key, request.ContentType, ChecksumAlgorithm: null, ChecksumType: null, NoHeaders, NoHeaders);
        var created = await uploads.CreateAsync(command, ct);
        return created.IsFailure ? created.Error : new StartUploadResponse(created.Value);
    }

    public async ValueTask<Result<UploadedPartResponse>> UploadPartAsync(
        string bucket, string key, string uploadId, int partNumber, long? length, Stream body, CancellationToken ct)
    {
        var command = new UploadPartCommand(bucket, key, uploadId, partNumber, length, ContentMd5: null);
        var part = await uploads.UploadPartAsync(command, body, static () => null, ct);
        return part.IsFailure ? part.Error : new UploadedPartResponse(partNumber, part.Value.ETag);
    }

    public async ValueTask<Result<ObjectWrittenResponse>> CompleteUploadAsync(
        string bucket, string key, string uploadId, CompleteUploadRequest request, CancellationToken ct)
    {
        var parts = request.Parts.Select(static p => new CompletedPart(p.PartNumber, p.ETag, Checksum: null)).ToArray();
        var completed = await uploads.CompleteAsync(new CompleteUploadCommand(bucket, key, uploadId, parts, OnlyIfAbsent: false), ct);
        return completed.IsFailure ? completed.Error : new ObjectWrittenResponse(key, completed.Value.ETag, completed.Value.VersionId);
    }

    public ValueTask<Result> AbortUploadAsync(string bucket, string key, string uploadId, CancellationToken ct) =>
        uploads.AbortAsync(bucket, key, uploadId, ct);
}
