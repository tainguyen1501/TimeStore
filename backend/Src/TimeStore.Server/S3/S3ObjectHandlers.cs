using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using TimeStore.Application.Features.Objects;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Objects;
using TimeStore.Protocol;
using TimeStore.Protocol.Http;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Xml;

namespace TimeStore.Server.S3;

/// <summary>S3 object operations. Glue only: read headers, call <see cref="IObjectService"/>, write the S3 response.</summary>
internal sealed class S3ObjectHandlers(IObjectService objects, IObjectCopyService copies, S3Authorization authorization)
{
    private const int CopyBufferSize = 128 * 1024;
    private const string BytesUnit = "bytes";
    private const string HttpDateFormat = "r"; // RFC 1123

    public async Task PutObjectAsync(HttpContext context, PutObject operation, S3Body body)
    {
        var headers = context.Request.Headers;

        // Conditional writes: only "If-None-Match: *" (create-only) is supported.
        if (headers.ContainsKey(S3HeaderNames.IfMatch)
            || (headers.TryGetValue(S3HeaderNames.IfNoneMatch, out var noneMatch) && noneMatch.ToString() != S3HeaderNames.Wildcard))
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.NotImplemented, S3Messages.NotImplemented, StatusCodes.Status501NotImplemented);
            return;
        }

        if (!S3ObjectHeaders.TryReadContentMd5(headers, out var contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.InvalidDigest);
            return;
        }

        var command = new PutObjectCommand(
            operation.Bucket,
            operation.Key,
            body.PayloadLength,
            context.Request.ContentType,
            contentMd5,
            S3ObjectHeaders.StoredHeaders(headers),
            S3ObjectHeaders.UserMetadata(headers),
            OnlyIfAbsent: noneMatch.Count > 0);

        var result = await objects.PutAsync(command, context.Request.Body, () => S3ObjectHeaders.ToObjectChecksum(body.VerifiedChecksum), context.RequestAborted);
        if (result.IsFailure)
        {
            await S3Errors.WriteAsync(context, result.Error);
            return;
        }

        context.Response.Headers.ETag = Quote(result.Value.ETag);
        SetVersion(context, result.Value.VersionId);
        if (result.Value.Checksum is { } checksum)
            context.Response.Headers[S3ObjectHeaders.ChecksumHeader(checksum.Algorithm)] = Convert.ToBase64String(checksum.Digest);
        await S3Responses.EmptyAsync(context);
    }

    /// <summary>
    /// CopyObject. The source is opened first and its <c>x-amz-copy-source-if-*</c> conditions are checked on that open
    /// version, which is then the one copied.
    /// </summary>
    public async Task CopyObjectAsync(HttpContext context, CopyObject operation)
    {
        var ct = context.RequestAborted;
        var headers = context.Request.Headers;

        // Conditional writes on the destination: only "If-None-Match: *" (create-only), as for PutObject.
        if (headers.ContainsKey(S3HeaderNames.IfMatch)
            || (headers.TryGetValue(S3HeaderNames.IfNoneMatch, out var noneMatch) && noneMatch.ToString() != S3HeaderNames.Wildcard))
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.NotImplemented, S3Messages.NotImplemented, StatusCodes.Status501NotImplemented);
            return;
        }

        var directive = Header(headers, S3HeaderNames.MetadataDirective);
        if (directive is not (null or S3HeaderNames.DirectiveCopy or S3HeaderNames.DirectiveReplace))
        {
            await S3Errors.WriteAsync(context, S3RequestErrors.InvalidArgument(S3HeaderNames.MetadataDirective));
            return;
        }

        string? algorithm = null;
        if (Header(headers, S3HeaderNames.ChecksumAlgorithm) is { } requested)
        {
            if (!ChecksumAlgorithms.TryParse(requested, out var parsed))
            {
                await S3Errors.WriteAsync(context, S3RequestErrors.InvalidRequest($"Checksum algorithm '{requested}' is not supported."));
                return;
            }

            algorithm = ChecksumAlgorithms.NameOf(parsed);
        }

        var source = S3CopySource.Parse(operation.CopySource);
        if (source.IsFailure)
        {
            await S3Errors.WriteAsync(context, source.Error);
            return;
        }

        if (!await authorization.AllowsAsync(context, S3Authorization.CopySource(source.Value), ct))
        {
            await S3Errors.WriteAsync(context, Application.Features.Policies.PolicyErrors.AccessDenied);
            return;
        }

        await using var opened = (await objects.OpenVersionAsync(source.Value.Bucket, source.Value.Key, source.Value.VersionId, ct)) is var o && o.IsSuccess ? o.Value : null;
        if (opened is null)
        {
            await S3Errors.WriteAsync(context, o.Error);
            return;
        }

        if (opened.Marker is { } marker)
        {
            // The latest version of the source is a delete marker: no such key; naming a marker: an invalid source.
            await S3Errors.WriteAsync(context, marker.Named ? ObjectErrors.CopySourceIsDeleteMarker : ObjectErrors.NotFound(source.Value.Key));
            return;
        }

        var reader = opened.Reader!;
        var outcome = S3Preconditions.Evaluate(
            Header(headers, S3HeaderNames.CopySourceIfMatch), Header(headers, S3HeaderNames.CopySourceIfNoneMatch),
            Header(headers, S3HeaderNames.CopySourceIfModifiedSince), Header(headers, S3HeaderNames.CopySourceIfUnmodifiedSince),
            reader.Object.ETag, reader.Object.CreatedAt);
        if (outcome != PreconditionOutcome.Proceed) // a copy has no 304: every failed condition is 412
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.PreconditionFailed, S3Messages.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            return;
        }

        var command = new CopyObjectCommand(
            operation.Bucket, operation.Key, directive == S3HeaderNames.DirectiveReplace, context.Request.ContentType,
            S3ObjectHeaders.StoredHeaders(headers), S3ObjectHeaders.UserMetadata(headers), algorithm,
            OnlyIfAbsent: noneMatch.Count > 0);
        var copied = await copies.CopyAsync(command, reader, ct);
        if (copied.IsFailure)
        {
            await S3Errors.WriteAsync(context, copied.Error);
            return;
        }

        var result = copied.Value;
        if (opened.VersionId is not null)
            context.Response.Headers[S3HeaderNames.CopySourceVersionId] = opened.VersionId;
        SetVersion(context, result.VersionId);
        if (result.Checksum is { } checksum)
            context.Response.Headers[S3ObjectHeaders.ChecksumHeader(checksum.Algorithm)] = Convert.ToBase64String(checksum.Digest);
        await S3Responses.XmlAsync(context, body => S3XmlWriter.CopyObjectResultAsync(
            body, result.ETag, result.LastModified, result.Checksum?.Algorithm,
            result.Checksum is { } c ? Convert.ToBase64String(c.Digest) : null));
    }

    public async Task DeleteObjectAsync(HttpContext context, DeleteObject operation)
    {
        var deleted = await objects.DeleteAsync(operation.Bucket, operation.Key, operation.VersionId, context.RequestAborted);
        if (deleted.IsFailure)
        {
            await S3Errors.WriteAsync(context, deleted.Error);
            return;
        }

        SetVersion(context, deleted.Value.VersionId);
        if (deleted.Value.DeleteMarker)
            context.Response.Headers[S3HeaderNames.DeleteMarker] = S3HeaderNames.True;
        await S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Multi-object delete. The document must be protected by Content-MD5 or a verified checksum (Amazon S3 rule);
    /// invalid keys are reported per key, the rest is deleted in one metadata transaction.
    /// </summary>
    public async Task DeleteObjectsAsync(HttpContext context, DeleteObjects operation, S3Body body)
    {
        var ct = context.RequestAborted;
        if (!S3ObjectHeaders.TryReadContentMd5(context.Request.Headers, out var contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.InvalidDigest);
            return;
        }

        var document = await S3XmlReader.ReadBodyAsync(context.Request.Body, S3XmlReader.MaxDeleteDocumentBytes, ct);
        if (document.IsFailure)
        {
            await S3Errors.WriteAsync(context, document.Error);
            return;
        }

        if (contentMd5 is null && body.VerifiedChecksum is null)
        {
            await S3Errors.WriteAsync(context, ObjectErrors.IntegrityRequired);
            return;
        }

        if (contentMd5 is not null && !MD5.HashData(document.Value).AsSpan().SequenceEqual(contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.BadDigest);
            return;
        }

        var request = await S3XmlReader.ParseDeleteAsync(document.Value, ct);
        if (request.IsFailure)
        {
            await S3Errors.WriteAsync(context, request.Error);
            return;
        }

        var entries = new List<DeleteObjectEntry>(request.Value.Objects.Count);
        var errors = new List<S3DeleteError>();
        var denied = Application.Features.Policies.PolicyErrors.AccessDenied;
        foreach (var entry in request.Value.Objects)
        {
            if (S3PercentDecoder.Utf8Length(entry.Key) > S3Limits.MaxKeyBytes)
                errors.Add(new S3DeleteError(entry.Key, S3ErrorCodes.KeyTooLongError, S3Messages.KeyTooLong));
            else if (!await authorization.AllowsAsync(context, S3Authorization.Object(operation.Bucket, entry.Key,
                         entry.VersionId is null ? Application.Features.Policies.S3Actions.DeleteObject
                             : Application.Features.Policies.S3Actions.DeleteObjectVersion), ct))
                errors.Add(new S3DeleteError(entry.Key, S3ErrorCodes.AccessDenied, denied.Message)); // checked per key
            else
                entries.Add(new DeleteObjectEntry(entry.Key, entry.VersionId));
        }

        var deleted = await objects.DeleteManyAsync(operation.Bucket, entries, ct);
        if (deleted.IsFailure)
        {
            await S3Errors.WriteAsync(context, deleted.Error);
            return;
        }

        var results = new List<S3Deleted>(deleted.Value.Count);
        for (var i = 0; i < deleted.Value.Count; i++)
        {
            var result = deleted.Value[i];
            if (result.Error is { } error)
            {
                var info = S3ErrorMap.Resolve(error);
                errors.Add(new S3DeleteError(result.Key, info.Code, error.Message));
                continue;
            }

            var named = entries[i].VersionId;
            results.Add(new S3Deleted(
                result.Key,
                named,
                result.DeleteMarker,
                result.DeleteMarker ? result.VersionId : null));
        }

        await S3Responses.XmlAsync(context,
            stream => S3XmlWriter.DeleteResultAsync(stream, results, errors, request.Value.Quiet));
    }

    public async Task HeadObjectAsync(HttpContext context, HeadObject operation)
    {
        if (await RejectRangeWithPartNumberAsync(context, operation.PartNumber))
            return;
        var head = await objects.HeadVersionAsync(operation.Bucket, operation.Key, operation.VersionId, context.RequestAborted);
        if (head.IsFailure)
        {
            await S3Errors.WriteAsync(context, head.Error);
            return;
        }

        if (head.Value.Marker is { } marker)
        {
            await DeleteMarkerAsync(context, operation.Key, marker);
            return;
        }

        SetVersion(context, head.Value.VersionId);
        await RespondAsync(context, head.Value.Object!, content: null, overrides: [], operation.PartNumber);
    }

    public async Task GetObjectAsync(HttpContext context, GetObject operation)
    {
        if (await RejectRangeWithPartNumberAsync(context, operation.PartNumber))
            return;
        var opened = await objects.OpenVersionAsync(operation.Bucket, operation.Key, operation.VersionId, context.RequestAborted);
        if (opened.IsFailure)
        {
            await S3Errors.WriteAsync(context, opened.Error);
            return;
        }

        await using var read = opened.Value;
        if (read.Marker is { } marker)
        {
            await DeleteMarkerAsync(context, operation.Key, marker);
            return;
        }

        SetVersion(context, read.VersionId);
        await RespondAsync(context, read.Reader!.Object, read.Reader.Content, operation.ResponseHeaders, operation.PartNumber);
    }

    /// <summary>
    /// A read that met a delete marker: the latest version is one (404 NoSuchKey), or the named version is one (405
    /// MethodNotAllowed, with its Last-Modified). Both say so in <c>x-amz-delete-marker</c> and name the marker.
    /// </summary>
    private static Task DeleteMarkerAsync(HttpContext context, string key, DeleteMarkerHit marker)
    {
        var headers = context.Response.Headers;
        headers[S3HeaderNames.DeleteMarker] = S3HeaderNames.True;
        headers[S3HeaderNames.VersionId] = marker.VersionId;
        if (!marker.Named)
            return S3Errors.WriteAsync(context, ObjectErrors.NotFound(key));
        headers.LastModified = marker.LastModified.ToString(HttpDateFormat, CultureInfo.InvariantCulture);
        return S3Errors.WriteAsync(context, S3ErrorCodes.MethodNotAllowed, S3Messages.MethodNotAllowed, StatusCodes.Status405MethodNotAllowed);
    }

    private static void SetVersion(HttpContext context, string? versionId)
    {
        if (versionId is not null)
            context.Response.Headers[S3HeaderNames.VersionId] = versionId;
    }

    /// <summary>
    /// Shared GET/HEAD response: preconditions (304/412), object headers, range (206/416), then the content for GET.
    /// </summary>
    private static async Task RespondAsync(
        HttpContext context, StoredObject stored, Stream? content, IReadOnlyList<KeyValuePair<string, string>> overrides,
        int? partNumber)
    {
        var request = context.Request.Headers;
        var outcome = S3Preconditions.Evaluate(
            Header(request, S3HeaderNames.IfMatch), Header(request, S3HeaderNames.IfNoneMatch),
            Header(request, S3HeaderNames.IfModifiedSince), Header(request, S3HeaderNames.IfUnmodifiedSince),
            stored.ETag, stored.CreatedAt);
        var response = context.Response;
        if (outcome == PreconditionOutcome.Failed)
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.PreconditionFailed, S3Messages.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            return;
        }

        if (outcome == PreconditionOutcome.NotModified)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.Headers.ETag = Quote(stored.ETag);
            response.Headers.LastModified = HttpDate(stored.CreatedAt);
            return;
        }

        // partNumber selects one part's bytes (206 + x-amz-mp-parts-count); part 1 of a single PUT is the whole object.
        RangeRequest range;
        if (partNumber is { } number)
        {
            if (PartRange(stored, number) is not { } part)
            {
                await S3Errors.WriteAsync(
                    context, S3ErrorCodes.InvalidPartNumber, S3Messages.InvalidPartNumber, StatusCodes.Status416RangeNotSatisfiable);
                return;
            }

            range = part;
            if (stored.Data is MultipartBlobs multipart)
                response.Headers[S3HeaderNames.PartsCount] = multipart.Parts.Count.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            range = S3Range.Parse(Header(request, HeaderNames.Range), stored.Size);
        }

        if (range.Kind == RangeKind.Unsatisfiable)
        {
            response.Headers.ContentRange = $"bytes */{stored.Size}";
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.InvalidRange, S3Messages.InvalidRange, StatusCodes.Status416RangeNotSatisfiable);
            return;
        }

        WriteObjectHeaders(response, stored, overrides,
            includeChecksum: range.Kind == RangeKind.Whole
                && Header(request, S3HeaderNames.ChecksumMode) == S3HeaderNames.ChecksumModeEnabled);

        var (offset, length) = range.Kind == RangeKind.Partial ? (range.Range.Start, range.Range.Length) : (0L, stored.Size);
        if (range.Kind == RangeKind.Partial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = range.Range.ContentRange(stored.Size);
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        response.ContentLength = length;
        if (content is not null && length > 0)
            await CopyAsync(content, offset, length, response.Body, context.RequestAborted);
    }

    private static void WriteObjectHeaders(
        HttpResponse response, StoredObject stored, IReadOnlyList<KeyValuePair<string, string>> overrides, bool includeChecksum)
    {
        var headers = response.Headers;
        headers.ContentType = stored.ContentType;
        headers.ETag = Quote(stored.ETag);
        headers.LastModified = HttpDate(stored.CreatedAt);
        headers.AcceptRanges = BytesUnit;
        foreach (var header in stored.Headers)
            headers[header.Name] = header.Value;
        foreach (var entry in stored.UserMetadata)
            headers[S3HeaderNames.UserMetadataPrefix + entry.Name] = S3MetadataHeaders.Value(entry.Value);
        if (includeChecksum && stored.ChecksumAlgorithm is { } algorithm && stored.Checksum is { } digest)
        {
            var parts = stored.Data is MultipartBlobs multipart ? multipart.Parts.Count : 1;
            headers[S3ObjectHeaders.ChecksumHeader(algorithm)] = S3ObjectHeaders.ChecksumValue(digest, stored.ChecksumType, parts);
            if (stored.ChecksumType is { } type)
                headers[S3HeaderNames.ChecksumType] = type;
        }
        foreach (var (name, value) in overrides)
            headers[name] = value;
    }

    /// <summary>Streams <paramref name="length"/> bytes from <paramref name="offset"/> through one pooled buffer.</summary>
    private static async Task CopyAsync(Stream source, long offset, long length, Stream destination, CancellationToken ct)
    {
        if (offset > 0)
            source.Seek(offset, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (length > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
                if (read == 0)
                    throw new IOException("The blob is shorter than its metadata says.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Amazon S3 refuses a Range header together with partNumber.</summary>
    private static async Task<bool> RejectRangeWithPartNumberAsync(HttpContext context, int? partNumber)
    {
        if (partNumber is null || !context.Request.Headers.ContainsKey(HeaderNames.Range))
            return false;
        await S3Errors.WriteAsync(
            context, S3ErrorCodes.InvalidRequest, S3Messages.RangeWithPartNumber, StatusCodes.Status400BadRequest);
        return true;
    }

    /// <summary>
    /// The bytes of part <paramref name="number"/> (by its own number: uploads may skip numbers), or null when the
    /// object has no such part. A single PUT has one part, number 1, covering the whole object.
    /// </summary>
    private static RangeRequest? PartRange(StoredObject stored, int number)
    {
        switch (stored.Data)
        {
            case SingleBlob:
                return number == 1 ? new RangeRequest(RangeKind.Whole, default) : null;
            case MultipartBlobs multipart:
                foreach (var part in multipart.Parts)
                {
                    if (part.PartNumber == number)
                        return new RangeRequest(RangeKind.Partial, new ByteRange(part.Offset, part.Blob.Size));
                }

                return null;
        }

        return null;
    }

    private static string? Header(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;

    private static string HttpDate(DateTime utc) => utc.ToString(HttpDateFormat, CultureInfo.InvariantCulture);

    private static string Quote(string etag) => $"\"{etag}\"";
}
