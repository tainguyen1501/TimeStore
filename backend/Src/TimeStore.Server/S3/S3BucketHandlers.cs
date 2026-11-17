using TimeStore.Application.Features.Lifecycle;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Policies;
using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Protocol;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Xml;

namespace TimeStore.Server.S3;

/// <summary>
/// S3 bucket operations. Glue only: read the protocol input, call <see cref="IBucketService"/>, write the S3 response.
/// </summary>
internal sealed class S3BucketHandlers(
    IBucketService buckets, IObjectService objects, ILifecycleService lifecycle, IBucketPolicyService policies,
    ServerOptions options)
{
    public async Task ListBucketsAsync(HttpContext context, SigV4Principal principal)
    {
        var list = await buckets.ListAsync(context.RequestAborted);
        var entries = new S3BucketEntry[list.Count];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new S3BucketEntry(list[i].Name, list[i].CreatedAt);

        await S3Responses.XmlAsync(context, body => S3XmlWriter.ListAllMyBucketsAsync(body, Owner(principal), entries));
    }

    public async Task CreateBucketAsync(HttpContext context, CreateBucket operation)
    {
        var location = await S3XmlReader.ReadCreateBucketConfigurationAsync(context.Request.Body, context.RequestAborted);
        if (location.IsFailure)
        {
            await S3Errors.WriteAsync(context, location.Error);
            return;
        }

        if (location.Value is { } requested && requested != options.S3.Region)
        {
            await S3Errors.WriteAsync(context, S3XmlErrors.InvalidLocationConstraint(requested, options.S3.Region));
            return;
        }

        var created = await buckets.CreateAsync(new CreateBucketRequest(operation.Bucket), context.RequestAborted);
        if (created.IsFailure)
        {
            await S3Errors.WriteAsync(context, created.Error);
            return;
        }

        context.Response.Headers.Location = "/" + operation.Bucket;
        await S3Responses.EmptyAsync(context);
    }

    public async Task DeleteBucketAsync(HttpContext context, DeleteBucket operation)
    {
        var deleted = await buckets.DeleteAsync(operation.Bucket, context.RequestAborted);
        await (deleted.IsFailure
            ? S3Errors.WriteAsync(context, deleted.Error)
            : S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent));
    }

    public async Task HeadBucketAsync(HttpContext context, HeadBucket operation)
    {
        if (await FindAsync(context, operation.Bucket) is null)
            return;
        context.Response.Headers[S3HeaderNames.BucketRegion] = options.S3.Region;
        await S3Responses.EmptyAsync(context);
    }

    public async Task GetBucketLocationAsync(HttpContext context, GetBucketLocation operation)
    {
        if (await FindAsync(context, operation.Bucket) is not null)
            await S3Responses.XmlAsync(context, body => S3XmlWriter.LocationConstraintAsync(body, options.S3.Region));
    }

    public async Task GetBucketVersioningAsync(HttpContext context, GetBucketVersioning operation)
    {
        var versioning = await buckets.GetVersioningAsync(operation.Bucket, context.RequestAborted);
        if (versioning.IsFailure)
        {
            await S3Errors.WriteAsync(context, versioning.Error);
            return;
        }

        var status = versioning.Value switch
        {
            BucketVersioning.Enabled => S3VersioningStatus.Enabled,
            BucketVersioning.Suspended => S3VersioningStatus.Suspended,
            _ => null, // never enabled: an empty configuration
        };
        await S3Responses.XmlAsync(context, body => S3XmlWriter.VersioningConfigurationAsync(body, status));
    }

    /// <summary>
    /// PutBucketVersioning: Enabled or Suspended. MFA delete cannot be enabled (TimeStore has no MFA devices); sending
    /// <c>Disabled</c> is accepted, it is what every bucket has.
    /// </summary>
    public async Task PutBucketVersioningAsync(HttpContext context, PutBucketVersioning operation)
    {
        var ct = context.RequestAborted;
        var document = await S3XmlReader.ReadBodyAsync(context.Request.Body, S3XmlReader.MaxSmallDocumentBytes, ct);
        if (document.IsFailure)
        {
            await S3Errors.WriteAsync(context, document.Error);
            return;
        }

        var request = await S3XmlReader.ParseVersioningAsync(document.Value, ct);
        if (request.IsFailure)
        {
            await S3Errors.WriteAsync(context, request.Error);
            return;
        }

        if (request.Value.MfaDelete is S3VersioningStatus.Enabled)
        {
            await S3Errors.WriteAsync(context, S3ErrorCodes.NotImplemented, S3Messages.NotImplemented, StatusCodes.Status501NotImplemented);
            return;
        }

        var versioning = request.Value.Status == S3VersioningStatus.Enabled ? BucketVersioning.Enabled : BucketVersioning.Suspended;
        var set = await buckets.SetVersioningAsync(operation.Bucket, versioning, ct);
        await (set.IsFailure ? S3Errors.WriteAsync(context, set.Error) : S3Responses.EmptyAsync(context));
    }

    /// <summary>ListObjects (v1): Marker in, NextMarker out (only with a delimiter, as Amazon S3 returns it).</summary>
    public async Task ListObjectsAsync(HttpContext context, ListObjects operation, SigV4Principal principal)
    {
        var page = await objects.ListAsync(
            operation.Bucket, operation.Prefix ?? string.Empty, operation.Delimiter, operation.Marker,
            operation.MaxKeys, context.RequestAborted);
        if (page.IsFailure)
        {
            await S3Errors.WriteAsync(context, page.Error);
            return;
        }

        var nextMarker = page.Value.IsTruncated && operation.Delimiter is not null ? page.Value.LastEntry : null;
        await S3Responses.XmlAsync(context,
            body => S3XmlWriter.ListObjectsAsync(body, operation, ToListing(page.Value, nextMarker), Owner(principal)));
    }

    /// <summary>
    /// ListObjectVersions: versions and delete markers, newest first per key. A version-id marker only makes sense with
    /// a key marker.
    /// </summary>
    public async Task ListObjectVersionsAsync(HttpContext context, ListObjectVersions operation, SigV4Principal principal)
    {
        if (operation.VersionIdMarker is { Length: > 0 } && operation.KeyMarker is null)
        {
            await S3Errors.WriteAsync(context, S3RequestErrors.InvalidRequest("A version-id marker cannot be specified without a key marker."));
            return;
        }

        var page = await objects.ListVersionsAsync(
            operation.Bucket, operation.Prefix ?? string.Empty, operation.Delimiter, operation.KeyMarker,
            operation.VersionIdMarker, operation.MaxKeys, context.RequestAborted);
        if (page.IsFailure)
        {
            await S3Errors.WriteAsync(context, page.Error);
            return;
        }

        var versions = page.Value.Versions
            .Select(static v => new S3VersionEntry(v.Key, v.VersionIdText, v.IsLatest, v.IsDeleteMarker, v.CreatedAt, v.ETag, v.Size))
            .ToArray();
        var listing = new S3VersionListing(
            versions, page.Value.CommonPrefixes, page.Value.IsTruncated, page.Value.NextKeyMarker, page.Value.NextVersionIdMarker);
        await S3Responses.XmlAsync(context, body => S3XmlWriter.ListVersionsAsync(body, operation, listing, Owner(principal)));
    }

    /// <summary>
    /// ListObjectsV2: the continuation token (opaque, base64url of the last entry) wins over StartAfter, which only
    /// applies to the first page.
    /// </summary>
    public async Task ListObjectsV2Async(HttpContext context, ListObjectsV2 operation, SigV4Principal principal)
    {
        var startAfter = operation.StartAfter;
        if (operation.ContinuationToken is { Length: > 0 } token) // an empty token is no token (Amazon S3)
        {
            if (!S3ContinuationToken.TryDecode(token, out startAfter))
            {
                await S3Errors.WriteAsync(context, S3RequestErrors.InvalidArgument(S3QueryNames.ContinuationToken));
                return;
            }
        }

        var page = await objects.ListAsync(
            operation.Bucket, operation.Prefix ?? string.Empty, operation.Delimiter, startAfter, operation.MaxKeys,
            context.RequestAborted);
        if (page.IsFailure)
        {
            await S3Errors.WriteAsync(context, page.Error);
            return;
        }

        var nextToken = page.Value.IsTruncated && page.Value.LastEntry is { } last ? S3ContinuationToken.Encode(last) : null;
        await S3Responses.XmlAsync(context,
            body => S3XmlWriter.ListObjectsV2Async(body, operation, ToListing(page.Value, nextToken), Owner(principal)));
    }

    private static S3ObjectListing ToListing(ObjectPage page, string? nextToken)
    {
        var entries = new S3ObjectEntry[page.Objects.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var stored = page.Objects[i];
            entries[i] = new S3ObjectEntry(stored.Key, stored.CreatedAt, stored.ETag, stored.Size, S3StorageClasses.Standard);
        }

        return new S3ObjectListing(entries, page.CommonPrefixes, page.IsTruncated, nextToken);
    }

    /// <summary>The bucket, or null after writing NoSuchBucket.</summary>
    private async Task<BucketResponse?> FindAsync(HttpContext context, string name)
    {
        var found = await buckets.GetAsync(name, context.RequestAborted);
        if (found.IsSuccess)
            return found.Value;

        await S3Errors.WriteAsync(context, found.Error);
        return null;
    }

    /// <summary>Single tenant: the authenticated key is the owner shown in listings.</summary>
    private static S3Owner Owner(SigV4Principal principal) => new(principal.AccessKeyId, principal.AccessKeyId);

    // --- Lifecycle (step 6c) ---------------------------------------------------------------------------------------

    private const string LifecycleDateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly string[] AcceptedDateFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.fffzzz"];

    public async Task GetBucketLifecycleAsync(HttpContext context, GetBucketLifecycle operation)
    {
        var rules = await lifecycle.GetAsync(operation.Bucket, context.RequestAborted);
        if (rules.IsFailure)
        {
            await S3Errors.WriteAsync(context, rules.Error);
            return;
        }

        var xml = rules.Value.Select(static r => new S3LifecycleRule(
            r.Id, r.Enabled, r.Filter.Prefix, r.Filter.SizeGreaterThan, r.Filter.SizeLessThan, false, r.ExpirationDays,
            r.ExpirationDate?.ToString(LifecycleDateFormat, System.Globalization.CultureInfo.InvariantCulture),
            r.ExpiredObjectDeleteMarker, r.NoncurrentDays, r.NewerNoncurrentVersions, r.AbortIncompleteUploadDays, false,
            r.Filter.PrefixInRule)).ToArray();
        await S3Responses.XmlAsync(context, body => S3XmlWriter.LifecycleConfigurationAsync(body, xml));
    }

    /// <summary>
    /// PutBucketLifecycleConfiguration: replaces the configuration. Like DeleteObjects, the document must be protected by
    /// Content-MD5 or a verified checksum (Amazon S3 rule).
    /// </summary>
    public async Task PutBucketLifecycleAsync(HttpContext context, PutBucketLifecycle operation, Protocol.Streaming.S3Body body)
    {
        var ct = context.RequestAborted;
        if (!S3ObjectHeaders.TryReadContentMd5(context.Request.Headers, out var contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.InvalidDigest);
            return;
        }

        var document = await S3XmlReader.ReadBodyAsync(context.Request.Body, S3XmlReader.MaxLifecycleDocumentBytes, ct);
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

        if (contentMd5 is not null && !System.Security.Cryptography.MD5.HashData(document.Value).AsSpan().SequenceEqual(contentMd5))
        {
            await S3Errors.WriteAsync(context, ObjectErrors.BadDigest);
            return;
        }

        var parsed = await S3XmlReader.ParseLifecycleAsync(document.Value, ct);
        if (parsed.IsFailure)
        {
            await S3Errors.WriteAsync(context, parsed.Error);
            return;
        }

        var rules = new List<Domain.Lifecycle.LifecycleRule>(parsed.Value.Count);
        foreach (var rule in parsed.Value)
        {
            DateTime? date = null;
            if (rule.ExpirationDate is { } text)
            {
                if (!DateTimeOffset.TryParseExact(text, AcceptedDateFormats, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                {
                    await S3Errors.WriteAsync(context, LifecycleErrors.Invalid($"'{text}' is not an ISO 8601 date."));
                    return;
                }

                date = at.UtcDateTime;
            }

            if (rule.HasTransition)
            {
                await S3Errors.WriteAsync(context, LifecycleErrors.Transitions);
                return;
            }

            if (rule.HasTagFilter)
            {
                await S3Errors.WriteAsync(context, LifecycleErrors.TagFilters);
                return;
            }

            rules.Add(new Domain.Lifecycle.LifecycleRule(
                rule.Id ?? string.Empty, rule.Enabled, new Domain.Lifecycle.LifecycleFilter(rule.Prefix, rule.SizeGreaterThan, rule.SizeLessThan, rule.PrefixInRule),
                rule.ExpirationDays, date, rule.ExpiredObjectDeleteMarker, rule.NoncurrentDays, rule.NewerNoncurrentVersions,
                rule.AbortIncompleteUploadDays));
        }

        var set = await lifecycle.SetAsync(operation.Bucket, rules, ct);
        await (set.IsFailure ? S3Errors.WriteAsync(context, set.Error) : S3Responses.EmptyAsync(context));
    }

    public async Task DeleteBucketLifecycleAsync(HttpContext context, DeleteBucketLifecycle operation)
    {
        var deleted = await lifecycle.DeleteAsync(operation.Bucket, context.RequestAborted);
        await (deleted.IsFailure
            ? S3Errors.WriteAsync(context, deleted.Error)
            : S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent));
    }

    // --- Bucket policy (step 6d) -----------------------------------------------------------------------------------

    private const string JsonContentType = "application/json";

    /// <summary>GetBucketPolicy: the document as stored, or 404 NoSuchBucketPolicy.</summary>
    public async Task GetBucketPolicyAsync(HttpContext context, GetBucketPolicy operation)
    {
        var policy = await policies.GetAsync(operation.Bucket, context.RequestAborted);
        if (policy.IsFailure)
        {
            await S3Errors.WriteAsync(context, policy.Error);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync(policy.Value, context.RequestAborted);
    }

    /// <summary>PutBucketPolicy: the body is the JSON document (at most 20 KB), validated before it is stored.</summary>
    public async Task PutBucketPolicyAsync(HttpContext context, PutBucketPolicy operation)
    {
        var ct = context.RequestAborted;
        var document = await S3XmlReader.ReadBodyAsync(context.Request.Body, BucketPolicy.MaxDocumentBytes, ct);
        if (document.IsFailure)
        {
            await S3Errors.WriteAsync(context, PolicyErrors.Malformed($"Policies must be at most {BucketPolicy.MaxDocumentBytes} bytes."));
            return;
        }

        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(document.Value);
        }
        catch (System.Text.DecoderFallbackException)
        {
            await S3Errors.WriteAsync(context, PolicyErrors.Malformed("The policy is not UTF-8 text."));
            return;
        }

        var set = await policies.SetAsync(operation.Bucket, text, ct);
        await (set.IsFailure
            ? S3Errors.WriteAsync(context, set.Error)
            : S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent));
    }

    public async Task DeleteBucketPolicyAsync(HttpContext context, DeleteBucketPolicy operation)
    {
        var deleted = await policies.DeleteAsync(operation.Bucket, context.RequestAborted);
        await (deleted.IsFailure
            ? S3Errors.WriteAsync(context, deleted.Error)
            : S3Responses.EmptyAsync(context, StatusCodes.Status204NoContent));
    }

    /// <summary>GetBucketPolicyStatus: whether the policy makes the bucket public; 404 without a policy.</summary>
    public async Task GetBucketPolicyStatusAsync(HttpContext context, GetBucketPolicyStatus operation)
    {
        var status = await policies.IsPublicAsync(operation.Bucket, context.RequestAborted);
        if (status.IsFailure)
        {
            await S3Errors.WriteAsync(context, status.Error);
            return;
        }

        await S3Responses.XmlAsync(context, body => S3XmlWriter.PolicyStatusAsync(body, status.Value));
    }
}
