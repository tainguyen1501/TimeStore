using TimeStore.Protocol.Http;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Objects;
using TimeStore.Primitives;
using TimeStore.Protocol;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Xml;

namespace TimeStore.Server.S3;

/// <summary>Multipart upload operations. Glue only: read headers, call <see cref="IUploadService"/>, write the response.</summary>
internal sealed class S3UploadHandlers(IUploadService uploads, IObjectService objects, S3Authorization authorization)
{
    public async Task CreateMultipartUploadAsync(HttpContext context, CreateMultipartUpload operation)
    {
        var headers = context.Request.Headers;
        var checksum = S3ChecksumTypes.Resolve(
            headers[S3HeaderNames.ChecksumAlgorithm].ToString(), headers[S3HeaderNames.ChecksumType].ToString());
        if (checksum.IsFailure)
        {
            await S3Errors.WriteAsync(context, checksum.Error);
            return;
        }

        var algorithm = checksum.Value is { } resolved ? ChecksumAlgorithms.NameOf(resolved.Algorithm) : null;
        var command = new CreateUploadCommand(
            operation.Bucket, operation.Key, context.Request.ContentType, algorithm, checksum.Value?.Type,
            S3ObjectHeaders.StoredHeaders(headers), S3ObjectHeaders.UserMetadata(headers));
        var created = await uploads.CreateAsync(command, context.RequestAborted);
        if (created.IsFailure)
        {
            await S3Errors.WriteAsync(context, created.Error);
            return;
        }

        if (algorithm is not null)
        {
            context.Response.Headers[S3HeaderNames.ChecksumAlgorithm] = algorithm;
            context.Response.Headers[S3HeaderNames.ChecksumType] = checksum.Value!.Value.Type;
        }

        await S3Responses.XmlAsync(context,
            body => S3XmlWriter.InitiateMultipartUploadAsync(body, operation.Bucket, operation.Key, created.Value));
    }

    public async Task UploadPartAsync(HttpContext context, UploadPart operation, S3Body body)
    {
        if (!S3ObjectHeaders.TryReadContentMd5(context.Request.Headers, out var contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.InvalidDigest);
            return;
        }

        var command = new UploadPartCommand(
            operation.Bucket, operation.Key, operation.UploadId, operation.PartNumber, body.PayloadLength, contentMd5);
        var result = await uploads.UploadPartAsync(
            command, context.Request.Body, () => S3ObjectHeaders.ToObjectChecksum(body.VerifiedChecksum), context.RequestAborted);
        if (result.IsFailure)
        {
            await S3Errors.WriteAsync(context, result.Error);
            return;
        }

        context.Response.Headers.ETag = $"\"{result.Value.ETag}\"";
        if (result.Value.Checksum is { } checksum)
            context.Response.Headers[S3ObjectHeaders.ChecksumHeader(checksum.Algorithm)] = Convert.ToBase64String(checksum.Digest);
        await S3Responses.EmptyAsync(context);
    }

    public async Task CompleteMultipartUploadAsync(HttpContext context, CompleteMultipartUpload operation, string rawTarget)
    {
        var ct = context.RequestAborted;
        var headers = context.Request.Headers;

        // Conditional writes: only "If-None-Match: *" (create-only) is supported, as for PutObject.
        if (headers.ContainsKey(S3HeaderNames.IfMatch)
            || (headers.TryGetValue(S3HeaderNames.IfNoneMatch, out var noneMatch) && noneMatch.ToString() != S3HeaderNames.Wildcard))
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.NotImplemented, S3Messages.NotImplemented, StatusCodes.Status501NotImplemented);
            return;
        }

        var document = await S3XmlReader.ReadBodyAsync(context.Request.Body, S3XmlReader.MaxCompleteDocumentBytes, ct);
        var request = document.IsSuccess ? await S3XmlReader.ParseCompleteAsync(document.Value, ct) : document.Error;
        if (request.IsFailure)
        {
            await S3Errors.WriteAsync(context, request.Error);
            return;
        }

        var parts = new CompletedPart[request.Value.Parts.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = request.Value.Parts[i];
            ObjectChecksum? claimed = null;
            if (part.ChecksumAlgorithm is { } name && part.Checksum is { } value)
            {
                // An unknown or undecodable part checksum can never match the stored part.
                if (!ChecksumAlgorithms.TryParse(name, out var algorithm) || !S3Checksums.TryDecode(value, algorithm, out var digest))
                {
                    await S3Errors.WriteAsync(context, UploadErrors.InvalidPart(part.PartNumber));
                    return;
                }

                claimed = new ObjectChecksum(ChecksumAlgorithms.NameOf(algorithm), digest);
            }

            parts[i] = new CompletedPart(part.PartNumber, part.ETag, claimed);
        }

        var expected = ExpectedObjectChecksum(headers);
        if (expected.IsFailure)
        {
            await S3Errors.WriteAsync(context, expected.Error);
            return;
        }

        var completed = await uploads.CompleteAsync(
            new CompleteUploadCommand(
                operation.Bucket, operation.Key, operation.UploadId, parts, OnlyIfAbsent: noneMatch.Count > 0, expected.Value),
            ct);
        if (completed.IsFailure)
        {
            await S3Errors.WriteAsync(context, completed.Error);
            return;
        }

        var result = completed.Value;
        if (result.VersionId is not null)
            context.Response.Headers[S3HeaderNames.VersionId] = result.VersionId;
        var path = rawTarget.AsSpan();
        if (path.IndexOf('?') is var query and >= 0)
            path = path[..query];
        var location = $"{context.Request.Scheme}://{context.Request.Host}{path}";
        await S3Responses.XmlAsync(context, body => S3XmlWriter.CompleteMultipartUploadAsync(body, new S3CompleteResult(
            location, operation.Bucket, operation.Key, result.ETag, result.Checksum?.Algorithm,
            result.Checksum is { } checksum ? S3ObjectHeaders.ChecksumValue(checksum.Digest, result.ChecksumType, result.PartsCount) : null,
            result.ChecksumType)));
    }

    /// <summary>
    /// The object checksum a CompleteMultipartUpload may carry as an <c>x-amz-checksum-*</c> header (the body pipeline
    /// leaves these headers alone on this operation). A composite value ends in <c>-N</c>, which is dropped here.
    /// </summary>
    private static Result<ObjectChecksum?> ExpectedObjectChecksum(IHeaderDictionary headers)
    {
        ObjectChecksum? found = null;
        foreach (var algorithm in Enum.GetValues<ChecksumAlgorithm>())
        {
            var name = S3Checksums.HeaderName(algorithm);
            if (!headers.TryGetValue(name, out var value))
                continue;
            var text = value.ToString();
            if (text.LastIndexOf('-') is var dash and > 0)
                text = text[..dash];
            if (found is not null)
                return S3BodyErrors.InvalidChecksum(name); // two object checksums
            if (!S3Checksums.TryDecode(text, algorithm, out var digest))
                return UploadErrors.BadObjectChecksum; // cannot match anything: BadDigest, as Amazon S3 answers
            found = new ObjectChecksum(ChecksumAlgorithms.NameOf(algorithm), digest);
        }

        return found;
    }

    public async Task AbortMultipartUploadAsync(HttpContext context, AbortMultipartUpload operation)
    {
        var aborted = await uploads.AbortAsync(operation.Bucket, operation.Key, operation.UploadId, context.RequestAborted);
        await (aborted.IsFailure
            ? S3Errors.WriteAsync(context, aborted.Error)
            : S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent));
    }

    public async Task ListPartsAsync(HttpContext context, ListParts operation, SigV4Principal principal)
    {
        var marker = operation.PartNumberMarker ?? 0;
        var listing = await uploads.ListPartsAsync(
            operation.Bucket, operation.Key, operation.UploadId, marker, operation.MaxParts, context.RequestAborted);
        if (listing.IsFailure)
        {
            await S3Errors.WriteAsync(context, listing.Error);
            return;
        }

        var (upload, page) = (listing.Value.Upload, listing.Value.Page);
        var parts = new S3PartEntry[page.Parts.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = page.Parts[i];
            parts[i] = new S3PartEntry(
                part.PartNumber, part.CreatedAt, part.ETag, part.Blob.Size,
                part.Checksum is null ? null : Convert.ToBase64String(part.Checksum));
        }

        var result = new S3PartListing(
            operation.Bucket, operation.Key, operation.UploadId, marker,
            page.IsTruncated && parts.Length > 0 ? parts[^1].PartNumber : null,
            operation.MaxParts, page.IsTruncated, parts, upload.ChecksumAlgorithm, upload.ChecksumType);
        await S3Responses.XmlAsync(context, body => S3XmlWriter.ListPartsAsync(body, result, Owner(principal)));
    }

    public async Task ListMultipartUploadsAsync(HttpContext context, ListMultipartUploads operation, SigV4Principal principal)
    {
        var page = await uploads.ListUploadsAsync(
            operation.Bucket, operation.Prefix ?? string.Empty, operation.Delimiter, operation.KeyMarker,
            operation.UploadIdMarker, operation.MaxUploads, context.RequestAborted);
        if (page.IsFailure)
        {
            await S3Errors.WriteAsync(context, page.Error);
            return;
        }

        var entries = new S3UploadEntry[page.Value.Uploads.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var upload = page.Value.Uploads[i];
            entries[i] = new S3UploadEntry(upload.Key, UploadIds.ToText(upload.UploadId), upload.CreatedAt);
        }

        var listing = new S3UploadListing(
            entries, page.Value.CommonPrefixes, page.Value.IsTruncated, page.Value.NextKeyMarker,
            page.Value.NextUploadIdMarker is { } id ? UploadIds.ToText(id) : null);
        await S3Responses.XmlAsync(context,
            body => S3XmlWriter.ListMultipartUploadsAsync(body, operation, listing, Owner(principal)));
    }

    private static S3Owner Owner(SigV4Principal principal) => new(principal.AccessKeyId, principal.AccessKeyId);

    /// <summary>
    /// UploadPartCopy: a slice (or all) of an existing object, optionally a named version, becomes a part. The source
    /// conditions are checked on the version that is copied.
    /// </summary>
    public async Task UploadPartCopyAsync(HttpContext context, UploadPartCopy operation)
    {
        var ct = context.RequestAborted;
        var headers = context.Request.Headers;
        var source = S3CopySource.Parse(operation.CopySource);
        if (source.IsFailure)
        {
            await S3Errors.WriteAsync(context, source.Error);
            return;
        }

        S3CopySourceRange? range = null;
        if (headers.TryGetValue(S3HeaderNames.CopySourceRange, out var rangeHeader))
        {
            if (!S3CopySourceRange.TryParse(rangeHeader.ToString(), out var parsed))
            {
                await S3Errors.WriteAsync(context, S3RequestErrors.InvalidArgument(S3HeaderNames.CopySourceRange));
                return;
            }

            range = parsed;
        }

        if (!await authorization.AllowsAsync(context, S3Authorization.CopySource(source.Value), ct))
        {
            await S3Errors.WriteAsync(context, Application.Features.Policies.PolicyErrors.AccessDenied);
            return;
        }

        var opened = await objects.OpenVersionAsync(source.Value.Bucket, source.Value.Key, source.Value.VersionId, ct);
        if (opened.IsFailure)
        {
            await S3Errors.WriteAsync(context, opened.Error);
            return;
        }

        await using var read = opened.Value;
        if (read.Marker is { } marker)
        {
            await S3Errors.WriteAsync(context, marker.Named ? ObjectErrors.CopySourceIsDeleteMarker : ObjectErrors.NotFound(source.Value.Key));
            return;
        }

        var reader = read.Reader!;
        var outcome = S3Preconditions.Evaluate(
            HeaderValue(headers, S3HeaderNames.CopySourceIfMatch), HeaderValue(headers, S3HeaderNames.CopySourceIfNoneMatch),
            HeaderValue(headers, S3HeaderNames.CopySourceIfModifiedSince), HeaderValue(headers, S3HeaderNames.CopySourceIfUnmodifiedSince),
            reader.Object.ETag, reader.Object.CreatedAt);
        if (outcome != PreconditionOutcome.Proceed)
        {
            await S3Errors.WriteAsync(
                context, S3ErrorCodes.PreconditionFailed, S3Messages.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            return;
        }

        var (offset, length) = range is { } r ? (r.First, r.Length) : (0L, reader.Object.Size);
        var copied = await uploads.UploadPartCopyAsync(
            new UploadPartCopyCommand(operation.Bucket, operation.Key, operation.UploadId, operation.PartNumber, offset, length),
            reader, ct);
        if (copied.IsFailure)
        {
            await S3Errors.WriteAsync(context, copied.Error);
            return;
        }

        if (read.VersionId is not null)
            context.Response.Headers[S3HeaderNames.CopySourceVersionId] = read.VersionId;
        var result = copied.Value;
        await S3Responses.XmlAsync(context, body => S3XmlWriter.CopyPartResultAsync(
            body, result.ETag, result.LastModified, result.Checksum?.Algorithm,
            result.Checksum is { } c ? Convert.ToBase64String(c.Digest) : null));
    }

    private static string? HeaderValue(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var value) ? value.ToString() : null;
}
