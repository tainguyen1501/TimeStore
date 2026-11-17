using TimeStore.Primitives;

namespace TimeStore.Protocol.Routing;

/// <summary>
/// Turns an HTTP request line into an <see cref="S3Operation"/>. Pure: no ASP.NET types, no I/O; the Server passes the
/// method, the host (without port), the raw request target exactly as sent, and the copy-source header.
/// <para>
/// Addressing: <c>bucket.&lt;domain&gt;/key</c> (virtual host) when the host is a sub-domain of a configured domain;
/// otherwise path style <c>/bucket/key</c>, also for IP addresses and unknown hosts.
/// </para>
/// </summary>
public sealed class S3Router
{
    private readonly string[] _domains;

    /// <param name="virtualHostDomains">Domains whose sub-domains name buckets (<c>s3.example.com</c>).</param>
    public S3Router(IEnumerable<string> virtualHostDomains) =>
        _domains = virtualHostDomains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .ToArray();

    public Result<S3Operation> Route(string method, string host, string rawTarget, string? copySource)
    {
        var queryStart = rawTarget.IndexOf('?');
        var rawPath = queryStart < 0 ? rawTarget.AsSpan() : rawTarget.AsSpan(0, queryStart);
        var rawQuery = queryStart < 0 ? ReadOnlySpan<char>.Empty : rawTarget.AsSpan(queryStart + 1);
        if (rawPath.IsEmpty || rawPath[0] != '/' || !S3Query.TryParse(rawQuery, out var query))
            return S3RequestErrors.InvalidUri;

        ReadOnlySpan<char> rawBucket;
        ReadOnlySpan<char> rawKey;
        if (VirtualHostBucket(host) is { } hostBucket)
        {
            rawBucket = hostBucket;
            rawKey = rawPath[1..];
        }
        else
        {
            var rest = rawPath[1..];
            var slash = rest.IndexOf('/');
            rawBucket = slash < 0 ? rest : rest[..slash];
            rawKey = slash < 0 ? [] : rest[(slash + 1)..];
        }

        if (rawBucket.IsEmpty)
            return method == S3Methods.Get ? new ListBuckets() : S3RequestErrors.MethodNotAllowed;

        if (!S3PercentDecoder.TryDecode(rawBucket, plusAsSpace: false, out var bucket)
            || !S3PercentDecoder.TryDecode(rawKey, plusAsSpace: false, out var key))
            return S3RequestErrors.InvalidUri;
        if (S3PercentDecoder.Utf8Length(key) > S3Limits.MaxKeyBytes)
            return S3RequestErrors.KeyTooLong;

        var keyOrNull = key.Length == 0 ? null : key;
        if (FirstUnsupported(query) is { } feature)
            return new UnsupportedOperation(bucket, keyOrNull, feature);
        if (CountOperations(query) > 1)
            return S3RequestErrors.AmbiguousSubresource;

        return keyOrNull is null
            ? RouteBucket(method, bucket, query)
            : RouteObject(method, bucket, keyOrNull, query, string.IsNullOrEmpty(copySource) ? null : copySource);
    }

    private static Result<S3Operation> RouteBucket(string method, string bucket, S3Query query)
    {
        var hasOperation = CountOperations(query) > 0;
        switch (method)
        {
            case S3Methods.Get:
                if (query.Contains(S3QueryNames.Uploads))
                    return ListUploads(bucket, query);
                if (query.Contains(S3QueryNames.Versions))
                    return ListVersions(bucket, query);
                if (query.Contains(S3QueryNames.Location))
                    return new GetBucketLocation(bucket);
                if (query.Contains(S3QueryNames.Versioning))
                    return new GetBucketVersioning(bucket);
                if (query.Contains(S3QueryNames.Lifecycle))
                    return new GetBucketLifecycle(bucket);
                if (query.Contains(S3QueryNames.Policy))
                    return new GetBucketPolicy(bucket);
                if (query.Contains(S3QueryNames.PolicyStatus))
                    return new GetBucketPolicyStatus(bucket);
                if (query.Get(S3QueryNames.ListType) is { } listType)
                    return listType == S3QueryNames.ListTypeV2
                        ? ListV2(bucket, query)
                        : S3RequestErrors.InvalidArgument(S3QueryNames.ListType);
                if (query.Contains(S3QueryNames.UploadId) || query.Contains(S3QueryNames.Delete))
                    return S3RequestErrors.MethodNotAllowed;
                return ListV1(bucket, query);

            case S3Methods.Put:
                if (query.Contains(S3QueryNames.Versioning))
                    return new PutBucketVersioning(bucket);
                if (query.Contains(S3QueryNames.Lifecycle))
                    return new PutBucketLifecycle(bucket);
                if (query.Contains(S3QueryNames.Policy))
                    return new PutBucketPolicy(bucket);
                return hasOperation ? S3RequestErrors.MethodNotAllowed : new CreateBucket(bucket);

            case S3Methods.Post:
                if (query.Contains(S3QueryNames.Delete))
                    return new DeleteObjects(bucket);
                return hasOperation
                    ? S3RequestErrors.MethodNotAllowed
                    : new UnsupportedOperation(bucket, null, S3Features.PostObject);

            case S3Methods.Delete:
                if (query.Contains(S3QueryNames.Lifecycle))
                    return new DeleteBucketLifecycle(bucket);
                if (query.Contains(S3QueryNames.Policy))
                    return new DeleteBucketPolicy(bucket);
                return hasOperation ? S3RequestErrors.MethodNotAllowed : new DeleteBucket(bucket);

            case S3Methods.Head:
                return hasOperation ? S3RequestErrors.MethodNotAllowed : new HeadBucket(bucket);

            default:
                return S3RequestErrors.MethodNotAllowed;
        }
    }

    private static Result<S3Operation> RouteObject(
        string method, string bucket, string key, S3Query query, string? copySource)
    {
        var uploadId = query.Get(S3QueryNames.UploadId);
        if (uploadId is { Length: 0 })
            return S3RequestErrors.InvalidArgument(S3QueryNames.UploadId);
        var versionId = query.Get(S3QueryNames.VersionId);

        switch (method)
        {
            case S3Methods.Get when uploadId is not null:
            {
                if (!TryLimit(query, S3QueryNames.MaxParts, out var maxParts, out var error)
                    || !TryOptionalInt(query, S3QueryNames.PartNumberMarker, 0, int.MaxValue, out var marker, out error))
                    return error;
                return new ListParts(bucket, key, uploadId, maxParts, marker);
            }

            case S3Methods.Get:
            case S3Methods.Head:
            {
                if (!TryOptionalInt(query, S3QueryNames.PartNumber, S3Limits.MinPartNumber, S3Limits.MaxPartNumber,
                        out var partNumber, out var error))
                    return error;
                return method == S3Methods.Get
                    ? new GetObject(bucket, key, versionId, partNumber, ResponseOverrides(query))
                    : new HeadObject(bucket, key, versionId, partNumber);
            }

            case S3Methods.Put when uploadId is not null || query.Contains(S3QueryNames.PartNumber):
            {
                if (uploadId is null)
                    return S3RequestErrors.MissingArgument(S3QueryNames.UploadId);
                if (!TryOptionalInt(query, S3QueryNames.PartNumber, S3Limits.MinPartNumber, S3Limits.MaxPartNumber,
                        out var partNumber, out var error))
                    return error;
                if (partNumber is not { } part)
                    return S3RequestErrors.MissingArgument(S3QueryNames.PartNumber);
                return copySource is null
                    ? new UploadPart(bucket, key, uploadId, part)
                    : new UploadPartCopy(bucket, key, uploadId, part, copySource);
            }

            case S3Methods.Put:
                return copySource is null ? new PutObject(bucket, key) : new CopyObject(bucket, key, copySource);

            case S3Methods.Post when query.Contains(S3QueryNames.Uploads):
                return new CreateMultipartUpload(bucket, key);

            case S3Methods.Post when uploadId is not null:
                return new CompleteMultipartUpload(bucket, key, uploadId);

            case S3Methods.Delete when uploadId is not null:
                return new AbortMultipartUpload(bucket, key, uploadId);

            case S3Methods.Delete:
                return new DeleteObject(bucket, key, versionId);

            default:
                return S3RequestErrors.MethodNotAllowed;
        }
    }

    private static Result<S3Operation> ListV1(string bucket, S3Query query) =>
        TryLimit(query, S3QueryNames.MaxKeys, out var maxKeys, out var error)
        && TryEncodeUrl(query, out var encodeUrl, out error)
            ? new ListObjects(
                bucket, query.Get(S3QueryNames.Prefix), NullIfEmpty(query.Get(S3QueryNames.Delimiter)),
                query.Get(S3QueryNames.Marker), maxKeys, encodeUrl)
            : error;

    private static Result<S3Operation> ListV2(string bucket, S3Query query) =>
        TryLimit(query, S3QueryNames.MaxKeys, out var maxKeys, out var error)
        && TryEncodeUrl(query, out var encodeUrl, out error)
            ? new ListObjectsV2(
                bucket, query.Get(S3QueryNames.Prefix), NullIfEmpty(query.Get(S3QueryNames.Delimiter)),
                query.Get(S3QueryNames.ContinuationToken), query.Get(S3QueryNames.StartAfter), maxKeys, encodeUrl,
                query.Get(S3QueryNames.FetchOwner) == S3QueryNames.True)
            : error;

    private static Result<S3Operation> ListVersions(string bucket, S3Query query) =>
        TryLimit(query, S3QueryNames.MaxKeys, out var maxKeys, out var error)
        && TryEncodeUrl(query, out var encodeUrl, out error)
            ? new ListObjectVersions(
                bucket, query.Get(S3QueryNames.Prefix), NullIfEmpty(query.Get(S3QueryNames.Delimiter)),
                query.Get(S3QueryNames.KeyMarker), query.Get(S3QueryNames.VersionIdMarker), maxKeys, encodeUrl)
            : error;

    private static Result<S3Operation> ListUploads(string bucket, S3Query query) =>
        TryLimit(query, S3QueryNames.MaxUploads, out var maxUploads, out var error)
        && TryEncodeUrl(query, out var encodeUrl, out error)
            ? new ListMultipartUploads(
                bucket, query.Get(S3QueryNames.Prefix), NullIfEmpty(query.Get(S3QueryNames.Delimiter)),
                query.Get(S3QueryNames.KeyMarker), query.Get(S3QueryNames.UploadIdMarker), maxUploads, encodeUrl)
            : error;

    /// <summary>Page size: absent → 1000; negative or not a number → InvalidArgument; above 1000 → 1000 (as S3).</summary>
    private static bool TryLimit(S3Query query, string name, out int value, out AppError error)
    {
        error = null!;
        value = S3Limits.MaxListKeys;
        var raw = query.Get(name);
        if (raw is null)
            return true;
        if (!int.TryParse(raw, out var parsed) || parsed < 0)
        {
            error = S3RequestErrors.InvalidArgument(name);
            return false;
        }

        value = Math.Min(parsed, S3Limits.MaxListKeys);
        return true;
    }

    private static bool TryOptionalInt(S3Query query, string name, int min, int max, out int? value, out AppError error)
    {
        error = null!;
        value = null;
        var raw = query.Get(name);
        if (raw is null)
            return true;
        if (!int.TryParse(raw, out var parsed) || parsed < min || parsed > max)
        {
            error = S3RequestErrors.InvalidArgument(name);
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryEncodeUrl(S3Query query, out bool encodeUrl, out AppError error)
    {
        error = null!;
        var raw = query.Get(S3QueryNames.EncodingType);
        encodeUrl = raw == S3QueryNames.EncodingTypeUrl;
        if (raw is null || encodeUrl)
            return true;
        error = S3RequestErrors.InvalidArgument(S3QueryNames.EncodingType);
        return false;
    }

    private static string? FirstUnsupported(S3Query query)
    {
        foreach (var name in query.Names)
        {
            if (S3QueryNames.Unsupported.Contains(name))
                return name;
        }

        return null;
    }

    private static int CountOperations(S3Query query)
    {
        var count = 0;
        foreach (var name in query.Names)
        {
            if (S3QueryNames.Operations.Contains(name))
                count++;
        }

        return count;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static IReadOnlyList<KeyValuePair<string, string>> ResponseOverrides(S3Query query)
    {
        List<KeyValuePair<string, string>>? overrides = null;
        foreach (var (parameter, header) in S3QueryNames.ResponseOverrides)
        {
            if (query.Get(parameter) is { } value)
                (overrides ??= []).Add(new(header, value));
        }

        return overrides ?? (IReadOnlyList<KeyValuePair<string, string>>)[];
    }

    private string? VirtualHostBucket(string host)
    {
        foreach (var domain in _domains)
        {
            if (host.Length > domain.Length + 1
                && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - domain.Length - 1] == '.')
                return host[..(host.Length - domain.Length - 1)];
        }

        return null;
    }
}
