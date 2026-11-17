using System.Globalization;
using System.Text;
using System.Xml;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;

namespace TimeStore.Protocol.Xml;

/// <summary>
/// S3 response documents, written by hand with <see cref="XmlWriter"/> (no <c>XmlSerializer</c>: AOT). Element names
/// and order follow the Amazon S3 API reference; every document carries the S3 namespace.
/// </summary>
public static class S3XmlWriter
{
    private static readonly XmlWriterSettings Settings = new()
    {
        Async = true,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    /// <summary><c>ListAllMyBucketsResult</c> (ListBuckets).</summary>
    public static Task ListAllMyBucketsAsync(Stream destination, S3Owner owner, IReadOnlyList<S3BucketEntry> buckets) =>
        WriteAsync(destination, Names.ListAllMyBucketsResult, async writer =>
        {
            await WriteOwnerAsync(writer, owner);
            await writer.WriteStartElementAsync(null, Names.Buckets, S3Xml.Namespace);
            foreach (var bucket in buckets)
            {
                await writer.WriteStartElementAsync(null, Names.Bucket, S3Xml.Namespace);
                await Element(writer, Names.Name, bucket.Name);
                await Element(writer, Names.CreationDate, Timestamp(bucket.CreatedAt));
                await writer.WriteEndElementAsync();
            }

            await writer.WriteEndElementAsync();
        });

    /// <summary><c>LocationConstraint</c> (GetBucketLocation): empty for us-east-1, as Amazon S3 answers.</summary>
    public static Task LocationConstraintAsync(Stream destination, string region) =>
        WriteAsync(destination, Names.LocationConstraint, writer =>
            region == UsEast1 ? Task.CompletedTask : writer.WriteStringAsync(region));

    /// <summary><c>VersioningConfiguration</c> (GetBucketVersioning); no status when versioning was never enabled.</summary>
    public static Task VersioningConfigurationAsync(Stream destination, string? status) =>
        WriteAsync(destination, Names.VersioningConfiguration, writer =>
            status is null ? Task.CompletedTask : Element(writer, Names.Status, status));

    /// <summary><c>ListBucketResult</c> for ListObjectsV2.</summary>
    public static Task ListObjectsV2Async(Stream destination, ListObjectsV2 request, S3ObjectListing listing, S3Owner owner) =>
        WriteAsync(destination, Names.ListBucketResult, async writer =>
        {
            var url = request.EncodeUrl;
            await Element(writer, Names.Name, request.Bucket);
            await Element(writer, Names.Prefix, Encode(request.Prefix ?? string.Empty, url));
            await Element(writer, Names.KeyCount, (listing.Objects.Count + listing.CommonPrefixes.Count).ToString(CultureInfo.InvariantCulture));
            await Element(writer, Names.MaxKeys, request.MaxKeys.ToString(CultureInfo.InvariantCulture));
            if (request.Delimiter is not null)
                await Element(writer, Names.Delimiter, Encode(request.Delimiter, url));
            await Element(writer, Names.IsTruncated, Bool(listing.IsTruncated));
            if (url)
                await Element(writer, Names.EncodingType, S3QueryNames.EncodingTypeUrl);
            if (request.ContinuationToken is not null)
                await Element(writer, Names.ContinuationToken, request.ContinuationToken);
            if (listing.IsTruncated && listing.NextToken is not null)
                await Element(writer, Names.NextContinuationToken, listing.NextToken);
            if (request.StartAfter is not null)
                await Element(writer, Names.StartAfter, Encode(request.StartAfter, url));
            await WriteEntriesAsync(writer, listing, url, request.FetchOwner ? owner : null);
        });

    /// <summary><c>ListBucketResult</c> for ListObjects (version 1); owners are always included.</summary>
    public static Task ListObjectsAsync(Stream destination, ListObjects request, S3ObjectListing listing, S3Owner owner) =>
        WriteAsync(destination, Names.ListBucketResult, async writer =>
        {
            var url = request.EncodeUrl;
            await Element(writer, Names.Name, request.Bucket);
            // Version 1 echoes the prefix as sent even with encoding-type=url (SDKs decode it only in version 2);
            // only characters XML cannot carry force encoding.
            var prefix = request.Prefix ?? string.Empty;
            await Element(writer, Names.Prefix, Encode(prefix, url && !IsXmlSafe(prefix)));
            await Element(writer, Names.Marker, Encode(request.Marker ?? string.Empty, url));
            await Element(writer, Names.MaxKeys, request.MaxKeys.ToString(CultureInfo.InvariantCulture));
            if (request.Delimiter is not null)
                await Element(writer, Names.Delimiter, Encode(request.Delimiter, url));
            await Element(writer, Names.IsTruncated, Bool(listing.IsTruncated));
            if (listing.IsTruncated && listing.NextToken is not null)
                await Element(writer, Names.NextMarker, Encode(listing.NextToken, url));
            if (url)
                await Element(writer, Names.EncodingType, S3QueryNames.EncodingTypeUrl);
            await WriteEntriesAsync(writer, listing, url, owner);
        });

    /// <summary>
    /// <c>ListVersionsResult</c> (ListObjectVersions): <c>Version</c> and <c>DeleteMarker</c> entries in key order,
    /// newest first within a key, then the common prefixes.
    /// </summary>
    public static Task ListVersionsAsync(
        Stream destination, ListObjectVersions request, S3VersionListing listing, S3Owner owner) =>
        WriteAsync(destination, Names.ListVersionsResult, async writer =>
        {
            var url = request.EncodeUrl;
            await Element(writer, Names.Name, request.Bucket);
            await Element(writer, Names.Prefix, Encode(request.Prefix ?? string.Empty, url));
            await Element(writer, Names.KeyMarker, Encode(request.KeyMarker ?? string.Empty, url));
            await Element(writer, Names.VersionIdMarker, request.VersionIdMarker ?? string.Empty);
            if (listing.IsTruncated && listing.NextKeyMarker is not null)
            {
                await Element(writer, Names.NextKeyMarker, Encode(listing.NextKeyMarker, url));
                if (listing.NextVersionIdMarker is not null)
                    await Element(writer, Names.NextVersionIdMarker, listing.NextVersionIdMarker);
            }

            await Element(writer, Names.MaxKeys, request.MaxKeys.ToString(CultureInfo.InvariantCulture));
            if (request.Delimiter is not null)
                await Element(writer, Names.Delimiter, Encode(request.Delimiter, url));
            await Element(writer, Names.IsTruncated, Bool(listing.IsTruncated));
            if (url)
                await Element(writer, Names.EncodingType, S3QueryNames.EncodingTypeUrl);
            foreach (var entry in listing.Versions)
            {
                await writer.WriteStartElementAsync(null, entry.IsDeleteMarker ? Names.DeleteMarker : Names.Version, S3Xml.Namespace);
                await Element(writer, Names.Key, Encode(entry.Key, url));
                await Element(writer, Names.VersionId, entry.VersionId);
                await Element(writer, Names.IsLatest, Bool(entry.IsLatest));
                await Element(writer, Names.LastModified, Timestamp(entry.LastModified));
                if (!entry.IsDeleteMarker)
                {
                    await Element(writer, Names.ETag, Quote(entry.ETag));
                    await Element(writer, Names.Size, entry.Size.ToString(CultureInfo.InvariantCulture));
                    await Element(writer, Names.StorageClass, S3StorageClasses.Standard);
                }

                await WriteOwnerAsync(writer, owner);
                await writer.WriteEndElementAsync();
            }

            foreach (var prefix in listing.CommonPrefixes)
            {
                await writer.WriteStartElementAsync(null, Names.CommonPrefixes, S3Xml.Namespace);
                await Element(writer, Names.Prefix, Encode(prefix, url));
                await writer.WriteEndElementAsync();
            }
        });

    /// <summary><c>DeleteResult</c> (DeleteObjects): deleted keys (unless quiet) and per-key errors.</summary>
    public static Task DeleteResultAsync(
        Stream destination, IReadOnlyList<S3Deleted> deleted, IReadOnlyList<S3DeleteError> errors, bool quiet) =>
        WriteAsync(destination, Names.DeleteResult, async writer =>
        {
            if (!quiet)
            {
                foreach (var entry in deleted)
                {
                    await writer.WriteStartElementAsync(null, Names.Deleted, S3Xml.Namespace);
                    await Element(writer, Names.Key, entry.Key);
                    if (entry.VersionId is not null)
                        await Element(writer, Names.VersionId, entry.VersionId);
                    if (entry.DeleteMarker)
                    {
                        await Element(writer, Names.DeleteMarker, S3QueryNames.True);
                        if (entry.DeleteMarkerVersionId is not null)
                            await Element(writer, Names.DeleteMarkerVersionId, entry.DeleteMarkerVersionId);
                    }

                    await writer.WriteEndElementAsync();
                }
            }

            foreach (var error in errors)
            {
                await writer.WriteStartElementAsync(null, S3Xml.Error, S3Xml.Namespace);
                await Element(writer, Names.Key, error.Key);
                await Element(writer, S3Xml.Code, error.Code);
                await Element(writer, S3Xml.Message, error.Message);
                await writer.WriteEndElementAsync();
            }
        });

    /// <summary><c>InitiateMultipartUploadResult</c> (CreateMultipartUpload).</summary>
    public static Task InitiateMultipartUploadAsync(Stream destination, string bucket, string key, string uploadId) =>
        WriteAsync(destination, Names.InitiateMultipartUploadResult, async writer =>
        {
            await Element(writer, Names.Bucket, bucket);
            await Element(writer, Names.Key, key);
            await Element(writer, Names.UploadId, uploadId);
        });

    /// <summary><c>CopyObjectResult</c> (CopyObject).</summary>
    /// <param name="checksumAlgorithm">Upper-case name naming the checksum element, or null.</param>
    public static Task CopyObjectResultAsync(
        Stream destination, string etag, DateTime lastModified, string? checksumAlgorithm, string? checksum) =>
        WriteAsync(destination, Names.CopyObjectResult, async writer =>
        {
            await Element(writer, Names.ETag, Quote(etag));
            await Element(writer, Names.LastModified, Timestamp(lastModified));
            if (checksumAlgorithm is not null && checksum is not null)
                await Element(writer, Names.ChecksumPrefix + checksumAlgorithm, checksum);
        });

    /// <summary><c>LifecycleConfiguration</c> (GetBucketLifecycleConfiguration), rules in configuration order.</summary>
    public static Task LifecycleConfigurationAsync(Stream destination, IReadOnlyList<S3LifecycleRule> rules) =>
        WriteAsync(destination, Names.LifecycleConfiguration, async writer =>
        {
            foreach (var rule in rules)
            {
                await writer.WriteStartElementAsync(null, Names.Rule, S3Xml.Namespace);
                await Element(writer, Names.RuleId, rule.Id ?? string.Empty);
                if (rule.PrefixInRule)
                {
                    await Element(writer, Names.Prefix, rule.Prefix); // the older form, returned as it was sent
                }
                else
                {
                    await writer.WriteStartElementAsync(null, Names.Filter, S3Xml.Namespace);
                    var sized = rule.SizeGreaterThan is not null || rule.SizeLessThan is not null;
                    if (sized)
                        await writer.WriteStartElementAsync(null, Names.And, S3Xml.Namespace);
                    await Element(writer, Names.Prefix, rule.Prefix);
                    if (rule.SizeGreaterThan is { } min)
                        await Element(writer, Names.ObjectSizeGreaterThan, min.ToString(CultureInfo.InvariantCulture));
                    if (rule.SizeLessThan is { } max)
                        await Element(writer, Names.ObjectSizeLessThan, max.ToString(CultureInfo.InvariantCulture));
                    if (sized)
                        await writer.WriteEndElementAsync();
                    await writer.WriteEndElementAsync();
                }
                await Element(writer, Names.Status, rule.Enabled ? S3VersioningStatus.Enabled : Names.Disabled);
                if (rule.ExpirationDays is not null || rule.ExpirationDate is not null || rule.ExpiredObjectDeleteMarker)
                {
                    await writer.WriteStartElementAsync(null, Names.Expiration, S3Xml.Namespace);
                    if (rule.ExpirationDays is { } days)
                        await Element(writer, Names.Days, days.ToString(CultureInfo.InvariantCulture));
                    if (rule.ExpirationDate is { } date)
                        await Element(writer, Names.Date, date);
                    if (rule.ExpiredObjectDeleteMarker)
                        await Element(writer, Names.ExpiredObjectDeleteMarker, S3QueryNames.True);
                    await writer.WriteEndElementAsync();
                }

                if (rule.NoncurrentDays is { } noncurrent)
                {
                    await writer.WriteStartElementAsync(null, Names.NoncurrentVersionExpiration, S3Xml.Namespace);
                    await Element(writer, Names.NoncurrentDays, noncurrent.ToString(CultureInfo.InvariantCulture));
                    if (rule.NewerNoncurrentVersions is { } newer)
                        await Element(writer, Names.NewerNoncurrentVersions, newer.ToString(CultureInfo.InvariantCulture));
                    await writer.WriteEndElementAsync();
                }

                if (rule.AbortIncompleteUploadDays is { } abort)
                {
                    await writer.WriteStartElementAsync(null, Names.AbortIncompleteMultipartUpload, S3Xml.Namespace);
                    await Element(writer, Names.DaysAfterInitiation, abort.ToString(CultureInfo.InvariantCulture));
                    await writer.WriteEndElementAsync();
                }

                await writer.WriteEndElementAsync();
            }
        });

    /// <summary><c>PolicyStatus</c> (GetBucketPolicyStatus).</summary>
    public static Task PolicyStatusAsync(Stream destination, bool isPublic) =>
        WriteAsync(destination, Names.PolicyStatus, writer => Element(writer, Names.IsPublic, Bool(isPublic)));

    /// <summary><c>CopyPartResult</c> (UploadPartCopy).</summary>
    public static Task CopyPartResultAsync(
        Stream destination, string etag, DateTime lastModified, string? checksumAlgorithm, string? checksum) =>
        WriteAsync(destination, Names.CopyPartResult, async writer =>
        {
            await Element(writer, Names.ETag, Quote(etag));
            await Element(writer, Names.LastModified, Timestamp(lastModified));
            if (checksumAlgorithm is not null && checksum is not null)
                await Element(writer, Names.ChecksumPrefix + checksumAlgorithm, checksum);
        });

    /// <summary><c>CompleteMultipartUploadResult</c> (CompleteMultipartUpload).</summary>
    public static Task CompleteMultipartUploadAsync(Stream destination, S3CompleteResult result) =>
        WriteAsync(destination, Names.CompleteMultipartUploadResult, async writer =>
        {
            await Element(writer, Names.Location, result.Location);
            await Element(writer, Names.Bucket, result.Bucket);
            await Element(writer, Names.Key, result.Key);
            await Element(writer, Names.ETag, Quote(result.ETag));
            if (result.ChecksumAlgorithm is { } algorithm && result.Checksum is { } checksum)
                await Element(writer, Names.ChecksumPrefix + algorithm, checksum);
            if (result.ChecksumType is { } type)
                await Element(writer, Names.ChecksumType, type);
        });

    /// <summary><c>ListPartsResult</c> (ListParts).</summary>
    public static Task ListPartsAsync(Stream destination, S3PartListing listing, S3Owner owner) =>
        WriteAsync(destination, Names.ListPartsResult, async writer =>
        {
            await Element(writer, Names.Bucket, listing.Bucket);
            await Element(writer, Names.Key, listing.Key);
            await Element(writer, Names.UploadId, listing.UploadId);
            await Element(writer, Names.PartNumberMarker, listing.PartNumberMarker.ToString(CultureInfo.InvariantCulture));
            if (listing.NextPartNumberMarker is { } next)
                await Element(writer, Names.NextPartNumberMarker, next.ToString(CultureInfo.InvariantCulture));
            await Element(writer, Names.MaxParts, listing.MaxParts.ToString(CultureInfo.InvariantCulture));
            await Element(writer, Names.IsTruncated, Bool(listing.IsTruncated));
            var checksumElement = listing.ChecksumAlgorithm is { } algorithm ? Names.ChecksumPrefix + algorithm : null;
            foreach (var part in listing.Parts)
            {
                await writer.WriteStartElementAsync(null, Names.Part, S3Xml.Namespace);
                await Element(writer, Names.PartNumber, part.PartNumber.ToString(CultureInfo.InvariantCulture));
                await Element(writer, Names.LastModified, Timestamp(part.LastModified));
                await Element(writer, Names.ETag, Quote(part.ETag));
                await Element(writer, Names.Size, part.Size.ToString(CultureInfo.InvariantCulture));
                if (checksumElement is not null && part.Checksum is not null)
                    await Element(writer, checksumElement, part.Checksum);
                await writer.WriteEndElementAsync();
            }

            await WriteInitiatorAsync(writer, owner);
            await WriteOwnerAsync(writer, owner);
            await Element(writer, Names.StorageClass, S3StorageClasses.Standard);
            if (listing.ChecksumAlgorithm is not null)
                await Element(writer, Names.ChecksumAlgorithm, listing.ChecksumAlgorithm);
            if (listing.ChecksumType is not null)
                await Element(writer, Names.ChecksumType, listing.ChecksumType);
        });

    /// <summary><c>ListMultipartUploadsResult</c> (ListMultipartUploads).</summary>
    public static Task ListMultipartUploadsAsync(
        Stream destination, ListMultipartUploads request, S3UploadListing listing, S3Owner owner) =>
        WriteAsync(destination, Names.ListMultipartUploadsResult, async writer =>
        {
            var url = request.EncodeUrl;
            await Element(writer, Names.Bucket, request.Bucket);
            await Element(writer, Names.KeyMarker, Encode(request.KeyMarker ?? string.Empty, url));
            await Element(writer, Names.UploadIdMarker, request.UploadIdMarker ?? string.Empty);
            if (listing.IsTruncated)
            {
                await Element(writer, Names.NextKeyMarker, Encode(listing.NextKeyMarker ?? string.Empty, url));
                await Element(writer, Names.NextUploadIdMarker, listing.NextUploadIdMarker ?? string.Empty);
            }

            await Element(writer, Names.Prefix, Encode(request.Prefix ?? string.Empty, url));
            if (request.Delimiter is not null)
                await Element(writer, Names.Delimiter, Encode(request.Delimiter, url));
            await Element(writer, Names.MaxUploads, request.MaxUploads.ToString(CultureInfo.InvariantCulture));
            await Element(writer, Names.IsTruncated, Bool(listing.IsTruncated));
            if (url)
                await Element(writer, Names.EncodingType, S3QueryNames.EncodingTypeUrl);
            foreach (var upload in listing.Uploads)
            {
                await writer.WriteStartElementAsync(null, Names.Upload, S3Xml.Namespace);
                await Element(writer, Names.Key, Encode(upload.Key, url));
                await Element(writer, Names.UploadId, upload.UploadId);
                await WriteInitiatorAsync(writer, owner);
                await WriteOwnerAsync(writer, owner);
                await Element(writer, Names.StorageClass, S3StorageClasses.Standard);
                await Element(writer, Names.Initiated, Timestamp(upload.Initiated));
                await writer.WriteEndElementAsync();
            }

            foreach (var prefix in listing.CommonPrefixes)
            {
                await writer.WriteStartElementAsync(null, Names.CommonPrefixes, S3Xml.Namespace);
                await Element(writer, Names.Prefix, Encode(prefix, url));
                await writer.WriteEndElementAsync();
            }
        });

    private static async Task WriteInitiatorAsync(XmlWriter writer, S3Owner owner)
    {
        await writer.WriteStartElementAsync(null, Names.Initiator, S3Xml.Namespace);
        await Element(writer, Names.Id, owner.Id);
        await Element(writer, Names.DisplayName, owner.DisplayName);
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteEntriesAsync(XmlWriter writer, S3ObjectListing listing, bool url, S3Owner? owner)
    {
        foreach (var entry in listing.Objects)
        {
            await writer.WriteStartElementAsync(null, Names.Contents, S3Xml.Namespace);
            await Element(writer, Names.Key, Encode(entry.Key, url));
            await Element(writer, Names.LastModified, Timestamp(entry.LastModified));
            await Element(writer, Names.ETag, Quote(entry.ETag));
            await Element(writer, Names.Size, entry.Size.ToString(CultureInfo.InvariantCulture));
            if (owner is not null)
                await WriteOwnerAsync(writer, owner);
            await Element(writer, Names.StorageClass, entry.StorageClass);
            await writer.WriteEndElementAsync();
        }

        foreach (var prefix in listing.CommonPrefixes)
        {
            await writer.WriteStartElementAsync(null, Names.CommonPrefixes, S3Xml.Namespace);
            await Element(writer, Names.Prefix, Encode(prefix, url));
            await writer.WriteEndElementAsync();
        }
    }

    private static async Task WriteOwnerAsync(XmlWriter writer, S3Owner owner)
    {
        await writer.WriteStartElementAsync(null, Names.Owner, S3Xml.Namespace);
        await Element(writer, Names.Id, owner.Id);
        await Element(writer, Names.DisplayName, owner.DisplayName);
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteAsync(Stream destination, string root, Func<XmlWriter, Task> body)
    {
        await using var writer = XmlWriter.Create(destination, Settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, root, S3Xml.Namespace);
        await body(writer);
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static Task Element(XmlWriter writer, string name, string value) =>
        writer.WriteElementStringAsync(null, name, S3Xml.Namespace, value);

    private static string Timestamp(DateTime utc) => utc.ToString(S3Xml.TimestampFormat, CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? S3QueryNames.True : False;

    private static string Quote(string etag) => $"\"{etag}\"";

    /// <summary>False for characters XML 1.0 cannot carry (C0 controls other than tab/LF/CR, U+FFFE, U+FFFF).</summary>
    private static bool IsXmlSafe(string value)
    {
        foreach (var c in value)
        {
            if ((c < ' ' && c is not '\t' and not '\n' and not '\r') || c is '\uFFFE' or '\uFFFF')
                return false;
        }

        return true;
    }

    private static string Encode(string value, bool url) => url ? SigV4Canonical.Encode(value, keepSlash: true) : value;

    private const string UsEast1 = "us-east-1";

    /// <summary>The version id of objects written while versioning was never enabled.</summary>
    public const string NullVersionId = "null";
    private const string False = "false";

    private static class Names
    {
        public const string ListAllMyBucketsResult = "ListAllMyBucketsResult";
        public const string ListBucketResult = "ListBucketResult";
        public const string LocationConstraint = "LocationConstraint";
        public const string VersioningConfiguration = "VersioningConfiguration";
        public const string Status = "Status";
        public const string Owner = "Owner";
        public const string Id = "ID";
        public const string DisplayName = "DisplayName";
        public const string Buckets = "Buckets";
        public const string Bucket = "Bucket";
        public const string Name = "Name";
        public const string CreationDate = "CreationDate";
        public const string Prefix = "Prefix";
        public const string Marker = "Marker";
        public const string NextMarker = "NextMarker";
        public const string KeyCount = "KeyCount";
        public const string MaxKeys = "MaxKeys";
        public const string Delimiter = "Delimiter";
        public const string IsTruncated = "IsTruncated";
        public const string EncodingType = "EncodingType";
        public const string ContinuationToken = "ContinuationToken";
        public const string NextContinuationToken = "NextContinuationToken";
        public const string StartAfter = "StartAfter";
        public const string Contents = "Contents";
        public const string Key = "Key";
        public const string LastModified = "LastModified";
        public const string ETag = "ETag";
        public const string Size = "Size";
        public const string StorageClass = "StorageClass";
        public const string CommonPrefixes = "CommonPrefixes";
        public const string DeleteResult = "DeleteResult";
        public const string Deleted = "Deleted";
        public const string ListVersionsResult = "ListVersionsResult";
        public const string Version = "Version";
        public const string DeleteMarker = "DeleteMarker";
        public const string CopyPartResult = "CopyPartResult";
        public const string PolicyStatus = "PolicyStatus";
        public const string IsPublic = "IsPublic";
        public const string LifecycleConfiguration = "LifecycleConfiguration";
        public const string Rule = "Rule";
        public const string RuleId = "ID";
        public const string Filter = "Filter";
        public const string And = "And";
        public const string ObjectSizeGreaterThan = "ObjectSizeGreaterThan";
        public const string ObjectSizeLessThan = "ObjectSizeLessThan";
        public const string Disabled = "Disabled";
        public const string Expiration = "Expiration";
        public const string Days = "Days";
        public const string Date = "Date";
        public const string ExpiredObjectDeleteMarker = "ExpiredObjectDeleteMarker";
        public const string NoncurrentVersionExpiration = "NoncurrentVersionExpiration";
        public const string NoncurrentDays = "NoncurrentDays";
        public const string NewerNoncurrentVersions = "NewerNoncurrentVersions";
        public const string AbortIncompleteMultipartUpload = "AbortIncompleteMultipartUpload";
        public const string DaysAfterInitiation = "DaysAfterInitiation";
        public const string DeleteMarkerVersionId = "DeleteMarkerVersionId";
        public const string VersionId = "VersionId";
        public const string IsLatest = "IsLatest";
        public const string VersionIdMarker = "VersionIdMarker";
        public const string NextVersionIdMarker = "NextVersionIdMarker";
        public const string InitiateMultipartUploadResult = "InitiateMultipartUploadResult";
        public const string CompleteMultipartUploadResult = "CompleteMultipartUploadResult";
        public const string CopyObjectResult = "CopyObjectResult";
        public const string Location = "Location";
        public const string ListPartsResult = "ListPartsResult";
        public const string ListMultipartUploadsResult = "ListMultipartUploadsResult";
        public const string UploadId = "UploadId";
        public const string PartNumberMarker = "PartNumberMarker";
        public const string NextPartNumberMarker = "NextPartNumberMarker";
        public const string MaxParts = "MaxParts";
        public const string Part = "Part";
        public const string PartNumber = "PartNumber";
        public const string Initiator = "Initiator";
        public const string ChecksumAlgorithm = "ChecksumAlgorithm";
        public const string ChecksumType = "ChecksumType";
        public const string ChecksumPrefix = "Checksum"; // + algorithm: ChecksumCRC32, ChecksumSHA256, ...
        public const string KeyMarker = "KeyMarker";
        public const string UploadIdMarker = "UploadIdMarker";
        public const string NextKeyMarker = "NextKeyMarker";
        public const string NextUploadIdMarker = "NextUploadIdMarker";
        public const string MaxUploads = "MaxUploads";
        public const string Upload = "Upload";
        public const string Initiated = "Initiated";
    }
}
