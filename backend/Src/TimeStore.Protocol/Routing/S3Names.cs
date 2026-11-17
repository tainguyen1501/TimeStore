using System.Collections.Frozen;

namespace TimeStore.Protocol.Routing;

/// <summary>HTTP methods as Kestrel reports them (upper case).</summary>
public static class S3Methods
{
    public const string Get = "GET";
    public const string Head = "HEAD";
    public const string Put = "PUT";
    public const string Post = "POST";
    public const string Delete = "DELETE";
}

/// <summary>Query parameters and sub-resources of the S3 REST API.</summary>
public static class S3QueryNames
{
    // Sub-resources that select an operation.
    public const string Uploads = "uploads";
    public const string UploadId = "uploadId";
    public const string Versions = "versions";
    public const string Versioning = "versioning";
    public const string Lifecycle = "lifecycle";
    public const string Policy = "policy";
    public const string PolicyStatus = "policyStatus";
    public const string Location = "location";
    public const string Delete = "delete";
    public const string ListType = "list-type";

    // Parameters.
    public const string PartNumber = "partNumber";
    public const string VersionId = "versionId";
    public const string ListTypeV2 = "2";
    public const string Prefix = "prefix";
    public const string Delimiter = "delimiter";
    public const string Marker = "marker";
    public const string MaxKeys = "max-keys";
    public const string ContinuationToken = "continuation-token";
    public const string StartAfter = "start-after";
    public const string FetchOwner = "fetch-owner";
    public const string EncodingType = "encoding-type";
    public const string EncodingTypeUrl = "url";
    public const string KeyMarker = "key-marker";
    public const string VersionIdMarker = "version-id-marker";
    public const string UploadIdMarker = "upload-id-marker";
    public const string MaxUploads = "max-uploads";
    public const string MaxParts = "max-parts";
    public const string PartNumberMarker = "part-number-marker";
    public const string True = "true";

    /// <summary>GET-object query parameters that override response headers, mapped to the header they set.</summary>
    public static readonly KeyValuePair<string, string>[] ResponseOverrides =
    [
        new("response-cache-control", "Cache-Control"),
        new("response-content-disposition", "Content-Disposition"),
        new("response-content-encoding", "Content-Encoding"),
        new("response-content-language", "Content-Language"),
        new("response-content-type", "Content-Type"),
        new("response-expires", "Expires"),
    ];

    /// <summary>Sub-resources that each select one operation; a request may carry at most one of them.</summary>
    public static readonly FrozenSet<string> Operations = FrozenSet.Create(
        StringComparer.Ordinal, Uploads, UploadId, Versions, Versioning, Lifecycle, Policy, PolicyStatus, Location, Delete, ListType);

    /// <summary>
    /// Sub-resources of the S3 API that TimeStore does not implement yet. A request carrying one is answered with
    /// <c>NotImplemented</c>.
    /// </summary>
    public static readonly FrozenSet<string> Unsupported = FrozenSet.Create(
        StringComparer.Ordinal,
        "acl", "accelerate", "analytics", "attributes", "cors", "encryption", "intelligent-tiering", "inventory",
        "legal-hold", "logging", "metrics", "notification", "object-lock", "ownershipControls", "publicAccessBlock", "replication", "requestPayment", "restore", "retention", "select",
        "tagging", "torrent", "website");
}

/// <summary>Features named in <see cref="UnsupportedOperation"/> that are not query sub-resources.</summary>
public static class S3Features
{
    /// <summary>Browser-based uploads (<c>POST /bucket</c> with a form).</summary>
    public const string PostObject = "post-object";
}

/// <summary>S3 limits the router enforces.</summary>
public static class S3Limits
{
    public const int MaxKeyBytes = 1024;
    public const int MaxListKeys = 1000;
    public const int MinPartNumber = 1;
    public const int MaxPartNumber = 10_000;
}

/// <summary>Request and response headers of the S3 protocol.</summary>
public static class S3HeaderNames
{
    /// <summary>Versioning (step 6a): the version a response is about, and whether it is a delete marker.</summary>
    public const string VersionId = "x-amz-version-id";
    public const string DeleteMarker = "x-amz-delete-marker";
    public const string CopySourceVersionId = "x-amz-copy-source-version-id";

    /// <summary>UploadPartCopy: the slice of the source to copy (<c>bytes=first-last</c>).</summary>
    public const string CopySourceRange = "x-amz-copy-source-range";

    /// <summary>The value of <see cref="DeleteMarker"/> when set.</summary>
    public const string True = "true";

    /// <summary>CopyObject: <c>COPY</c> (default: the source's metadata) or <c>REPLACE</c> (the request's).</summary>
    public const string MetadataDirective = "x-amz-metadata-directive";
    public const string DirectiveCopy = "COPY";
    public const string DirectiveReplace = "REPLACE";

    /// <summary>CopyObject conditions, evaluated against the source object.</summary>
    public const string CopySourceIfMatch = "x-amz-copy-source-if-match";
    public const string CopySourceIfNoneMatch = "x-amz-copy-source-if-none-match";
    public const string CopySourceIfModifiedSince = "x-amz-copy-source-if-modified-since";
    public const string CopySourceIfUnmodifiedSince = "x-amz-copy-source-if-unmodified-since";

    /// <summary>GET/HEAD with <c>partNumber</c> on a multipart object: how many parts it has.</summary>
    public const string PartsCount = "x-amz-mp-parts-count";

    /// <summary>CreateMultipartUpload: the checksum algorithm every part uses.</summary>
    public const string ChecksumAlgorithm = "x-amz-checksum-algorithm";

    /// <summary>CreateMultipartUpload: <c>COMPOSITE</c> or <c>FULL_OBJECT</c>.</summary>
    public const string ChecksumType = "x-amz-checksum-type";

    public const string ContentMd5 = "Content-MD5";
    public const string IfMatch = "If-Match";
    public const string IfNoneMatch = "If-None-Match";
    public const string IfModifiedSince = "If-Modified-Since";
    public const string IfUnmodifiedSince = "If-Unmodified-Since";
    public const string Wildcard = "*";
    public const string UserMetadataPrefix = "x-amz-meta-";
    public const string ChecksumMode = "x-amz-checksum-mode";
    public const string ChecksumModeEnabled = "ENABLED";

    /// <summary>System headers stored with an object and returned on GET/HEAD (lower case).</summary>
    public static readonly string[] StoredObjectHeaders =
        ["cache-control", "content-disposition", "content-encoding", "content-language", "expires"];

    public const string CopySource = "x-amz-copy-source";
    public const string RequestId = "x-amz-request-id";
    public const string HostId = "x-amz-id-2";
    public const string BucketRegion = "x-amz-bucket-region";
}
