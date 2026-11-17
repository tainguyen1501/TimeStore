using System.Globalization;
using System.Xml;
using TimeStore.Primitives;

namespace TimeStore.Protocol.Xml;

/// <summary>XML request bodies: malformed or oversized documents.</summary>
public static class S3XmlErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string MalformedXml = "S3_XML_MALFORMED";
        public const string InvalidLocationConstraint = "S3_XML_INVALID_LOCATION_CONSTRAINT";
    }

    public static readonly AppError MalformedXml = AppError.Validation(
        Codes.MalformedXml, "The XML you provided was not well-formed or did not validate against our published schema.");

    public static AppError InvalidLocationConstraint(string requested, string region) => AppError.Validation(
        Codes.InvalidLocationConstraint,
        $"The specified location-constraint '{requested}' is not valid; this server serves '{region}'.");
}

/// <summary>
/// Reads S3 request documents with a hardened <see cref="XmlReader"/>: no DTD, no external resolution (XXE),
/// bounded size. Namespaces are accepted with or without the S3 namespace, as Amazon S3 does.
/// </summary>
public static class S3XmlReader
{
    /// <summary>Largest small request document (bucket configuration, versioning). Delete lists get their own limit.</summary>
    public const int MaxSmallDocumentBytes = 64 * 1024;

    /// <summary>Largest multi-object delete document (1000 keys of up to 1024 bytes, XML-escaped).</summary>
    public const int MaxDeleteDocumentBytes = 2 * 1024 * 1024;

    /// <summary>Amazon S3 limit of keys in one multi-object delete.</summary>
    public const int MaxDeleteKeys = 1000;

    /// <summary>Largest CompleteMultipartUpload document: 10 000 parts with every checksum element fit well below.</summary>
    public const int MaxCompleteDocumentBytes = 4 * 1024 * 1024;

    /// <summary>Most parts one completion can list (Amazon S3 part number limit).</summary>
    public const int MaxCompleteParts = 10_000;

    /// <summary>A lifecycle configuration of 1000 rules stays well below this.</summary>
    public const int MaxLifecycleDocumentBytes = 1024 * 1024;

    private const string CompleteMultipartUpload = "CompleteMultipartUpload";
    private const string Part = "Part";
    private const string PartNumber = "PartNumber";
    private const string ETag = "ETag";
    private const string ChecksumPrefix = "Checksum"; // ChecksumCRC32, ChecksumSHA256, ...

    private const string CreateBucketConfiguration = "CreateBucketConfiguration";
    private const string LocationConstraint = "LocationConstraint";
    private const string Delete = "Delete";
    private const string Quiet = "Quiet";
    private const string Object = "Object";
    private const string Key = "Key";
    private const string VersionId = "VersionId";
    private const string True = "true";
    private const string VersioningConfiguration = "VersioningConfiguration";
    private const string Status = "Status";
    private const string MfaDelete = "MfaDelete";
    private const string Enabled = "Enabled";
    private const string Suspended = "Suspended";

    private static readonly XmlReaderSettings Settings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        // Kept: a key may be nothing but spaces (<Key> </Key>), and ignoring whitespace would turn it into an empty
        // key. The parsers skip every node that is not an element, so whitespace between elements is harmless.
        IgnoreWhitespace = false,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    /// <summary>
    /// The <c>LocationConstraint</c> of a CreateBucket body: null for an empty body or no constraint (default region).
    /// </summary>
    public static async Task<Result<string?>> ReadCreateBucketConfigurationAsync(Stream body, CancellationToken ct)
    {
        var buffered = await ReadBodyAsync(body, MaxSmallDocumentBytes, ct);
        if (buffered.IsFailure)
            return buffered.Error;
        if (buffered.Value.Length == 0)
            return (string?)null; // empty body: no configuration

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(buffered.Value), Settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != CreateBucketConfiguration)
                return S3XmlErrors.MalformedXml;

            string? location = null;
            if (reader.IsEmptyElement)
                return location;

            var depth = reader.Depth;
            while (await reader.ReadAsync() && reader.Depth > depth)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == LocationConstraint)
                    location = (await reader.ReadElementContentAsStringAsync()).Trim();
            }

            return string.IsNullOrEmpty(location) ? (string?)null : location;
        }
        catch (XmlException)
        {
            return S3XmlErrors.MalformedXml;
        }
    }

    /// <summary>
    /// Parses a multi-object delete document (<c>&lt;Delete&gt;&lt;Quiet/&gt;&lt;Object&gt;&lt;Key/&gt;...</c>). Keys are
    /// taken verbatim (no trimming: spaces are valid key characters). More than 1000 objects or an object without a
    /// key is MalformedXML, as in Amazon S3.
    /// </summary>
    public static async Task<Result<S3DeleteRequest>> ParseDeleteAsync(byte[] document, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(document), Settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != Delete || reader.IsEmptyElement)
                return S3XmlErrors.MalformedXml;

            var quiet = false;
            var entries = new List<S3DeleteEntry>();
            var depth = reader.Depth;
            await reader.ReadAsync();
            while (reader.Depth > depth)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync();
                    continue;
                }

                if (reader.LocalName == Quiet)
                {
                    quiet = (await reader.ReadElementContentAsStringAsync()).Trim() == True;
                }
                else if (reader.LocalName == Object)
                {
                    if (entries.Count == MaxDeleteKeys || await ReadObjectAsync(reader) is not { } entry)
                        return S3XmlErrors.MalformedXml;
                    entries.Add(entry);
                }
                else
                {
                    await reader.SkipAsync();
                }
            }

            return entries.Count == 0 ? S3XmlErrors.MalformedXml : new S3DeleteRequest(quiet, entries);
        }
        catch (XmlException)
        {
            return S3XmlErrors.MalformedXml;
        }
    }

    /// <summary>
    /// Parses a PutBucketVersioning document (<c>&lt;VersioningConfiguration&gt;&lt;Status&gt;Enabled|Suspended</c>,
    /// optional <c>MfaDelete</c>). Any other status, or none, is MalformedXML (as Amazon S3).
    /// </summary>
    public static async Task<Result<S3VersioningRequest>> ParseVersioningAsync(byte[] document, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(document), Settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != VersioningConfiguration || reader.IsEmptyElement)
                return S3XmlErrors.MalformedXml;

            string? status = null, mfa = null;
            var depth = reader.Depth;
            await reader.ReadAsync();
            while (reader.Depth > depth)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    await reader.ReadAsync();
                else if (reader.LocalName == Status)
                    status = (await reader.ReadElementContentAsStringAsync()).Trim();
                else if (reader.LocalName == MfaDelete)
                    mfa = (await reader.ReadElementContentAsStringAsync()).Trim();
                else
                    await reader.SkipAsync();
            }

            return status is Enabled or Suspended ? new S3VersioningRequest(status, mfa) : S3XmlErrors.MalformedXml;
        }
        catch (XmlException)
        {
            return S3XmlErrors.MalformedXml;
        }
    }

    /// <summary>
    /// Parses a lifecycle configuration (<c>&lt;LifecycleConfiguration&gt;&lt;Rule&gt;…</c>). Structure errors are
    /// MalformedXML (no rule, a status other than <c>Enabled</c>/<c>Disabled</c>, a number that is not one); value rules
    /// (ids, days, dates) are the caller's.
    /// </summary>
    public static async Task<Result<IReadOnlyList<S3LifecycleRule>>> ParseLifecycleAsync(byte[] document, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(document), Settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != LifecycleNames.Configuration)
                return S3XmlErrors.MalformedXml;
            var root = await ReadTreeAsync(reader, ct);
            var rules = new List<S3LifecycleRule>();
            foreach (var node in root.All(LifecycleNames.Rule))
            {
                if (ToRule(node) is not { } rule)
                    return S3XmlErrors.MalformedXml;
                rules.Add(rule);
            }

            return rules.Count == 0 ? S3XmlErrors.MalformedXml : Result<IReadOnlyList<S3LifecycleRule>>.Ok(rules);
        }
        catch (XmlException)
        {
            return S3XmlErrors.MalformedXml;
        }
    }

    private static S3LifecycleRule? ToRule(XmlTree rule)
    {
        var status = rule.Text(LifecycleNames.Status);
        if (status is not (LifecycleNames.Enabled or LifecycleNames.Disabled))
            return null;

        // The filter: <Filter> with Prefix, sizes, Tag, or an <And> of them; or the older <Prefix> in the rule.
        var prefix = rule.Text(LifecycleNames.Prefix);
        var prefixInRule = prefix is not null && rule.First(LifecycleNames.Filter) is null;
        long? greater = null, less = null;
        var hasTag = false;
        if (rule.First(LifecycleNames.Filter) is { } filter)
        {
            var terms = filter.First(LifecycleNames.And) ?? filter;
            prefix ??= terms.Text(LifecycleNames.Prefix);
            hasTag = terms.First(LifecycleNames.Tag) is not null;
            if (!TryLong(terms.Text(LifecycleNames.SizeGreaterThan), out greater) || !TryLong(terms.Text(LifecycleNames.SizeLessThan), out less))
                return null;
        }

        int? days = null, noncurrentDays = null, newer = null, abortDays = null;
        string? date = null;
        var markers = false;
        if (rule.First(LifecycleNames.Expiration) is { } expiration)
        {
            if (!TryInt(expiration.Text(LifecycleNames.Days), out days))
                return null;
            date = expiration.Text(LifecycleNames.Date);
            markers = expiration.Text(LifecycleNames.ExpiredObjectDeleteMarker) == True;
        }

        if (rule.First(LifecycleNames.NoncurrentVersionExpiration) is { } noncurrent
            && (!TryInt(noncurrent.Text(LifecycleNames.NoncurrentDays), out noncurrentDays)
                || !TryInt(noncurrent.Text(LifecycleNames.NewerNoncurrentVersions), out newer)))
            return null;

        if (rule.First(LifecycleNames.AbortIncompleteMultipartUpload) is { } abort
            && !TryInt(abort.Text(LifecycleNames.DaysAfterInitiation), out abortDays))
            return null;

        var transition = rule.First(LifecycleNames.Transition) is not null
            || rule.First(LifecycleNames.NoncurrentVersionTransition) is not null;
        return new S3LifecycleRule(
            rule.Text(LifecycleNames.Id), status == LifecycleNames.Enabled, prefix ?? string.Empty, greater, less, hasTag,
            days, date, markers, noncurrentDays, newer, abortDays, transition, prefixInRule);
    }

    private static bool TryInt(string? text, out int? value)
    {
        value = null;
        if (text is null)
            return true;
        if (!int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryLong(string? text, out long? value)
    {
        value = null;
        if (text is null)
            return true;
        if (!long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>Reads the current element and everything in it (lifecycle documents are small and nested).</summary>
    private static async Task<XmlTree> ReadTreeAsync(XmlReader reader, CancellationToken ct)
    {
        var node = new XmlTree(reader.LocalName);
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync();
            return node;
        }

        var depth = reader.Depth;
        await reader.ReadAsync();
        var text = new System.Text.StringBuilder();
        while (reader.Depth > depth)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
                node.Children.Add(await ReadTreeAsync(reader, ct));
            else
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    text.Append(reader.Value);
                await reader.ReadAsync();
            }
        }

        await reader.ReadAsync(); // the end element
        node.Value = text.ToString().Trim();
        return node;
    }

    private sealed class XmlTree(string name)
    {
        public string Name { get; } = name;

        public string Value { get; set; } = string.Empty;

        public List<XmlTree> Children { get; } = [];

        public XmlTree? First(string name) => Children.FirstOrDefault(c => c.Name == name);

        public IEnumerable<XmlTree> All(string name) => Children.Where(c => c.Name == name);

        public string? Text(string name) => First(name)?.Value;
    }

    private static class LifecycleNames
    {
        public const string Configuration = "LifecycleConfiguration";
        public const string Rule = "Rule";
        public const string Id = "ID";
        public const string Status = "Status";
        public const string Enabled = "Enabled";
        public const string Disabled = "Disabled";
        public const string Prefix = "Prefix";
        public const string Filter = "Filter";
        public const string And = "And";
        public const string Tag = "Tag";
        public const string SizeGreaterThan = "ObjectSizeGreaterThan";
        public const string SizeLessThan = "ObjectSizeLessThan";
        public const string Expiration = "Expiration";
        public const string Days = "Days";
        public const string Date = "Date";
        public const string ExpiredObjectDeleteMarker = "ExpiredObjectDeleteMarker";
        public const string NoncurrentVersionExpiration = "NoncurrentVersionExpiration";
        public const string NoncurrentDays = "NoncurrentDays";
        public const string NewerNoncurrentVersions = "NewerNoncurrentVersions";
        public const string AbortIncompleteMultipartUpload = "AbortIncompleteMultipartUpload";
        public const string DaysAfterInitiation = "DaysAfterInitiation";
        public const string Transition = "Transition";
        public const string NoncurrentVersionTransition = "NoncurrentVersionTransition";
    }

    /// <summary>
    /// Parses a CompleteMultipartUpload document (<c>&lt;CompleteMultipartUpload&gt;&lt;Part&gt;&lt;PartNumber/&gt;
    /// &lt;ETag/&gt;...</c>). Parts are returned in document order (the order rules are the service's). A part without
    /// a number or an ETag, a number that is not an integer, no parts or too many parts is MalformedXML.
    /// </summary>
    public static async Task<Result<S3CompleteRequest>> ParseCompleteAsync(byte[] document, CancellationToken ct)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(document), Settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != CompleteMultipartUpload || reader.IsEmptyElement)
                return S3XmlErrors.MalformedXml;

            var parts = new List<S3CompletePart>();
            var depth = reader.Depth;
            await reader.ReadAsync();
            while (reader.Depth > depth)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync();
                    continue;
                }

                if (reader.LocalName != Part)
                {
                    await reader.SkipAsync();
                    continue;
                }

                if (parts.Count == MaxCompleteParts || await ReadPartAsync(reader) is not { } part)
                    return S3XmlErrors.MalformedXml;
                parts.Add(part);
            }

            return parts.Count == 0 ? S3XmlErrors.MalformedXml : new S3CompleteRequest(parts);
        }
        catch (XmlException)
        {
            return S3XmlErrors.MalformedXml;
        }
    }

    /// <summary>One <c>&lt;Part&gt;</c>; leaves the reader after it. Null when it is incomplete or malformed.</summary>
    private static async Task<S3CompletePart?> ReadPartAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync();
            return null;
        }

        string? number = null, etag = null, checksumName = null, checksum = null;
        var depth = reader.Depth;
        await reader.ReadAsync();
        while (reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element)
                await reader.ReadAsync();
            else if (reader.LocalName == PartNumber)
                number = (await reader.ReadElementContentAsStringAsync()).Trim();
            else if (reader.LocalName == ETag)
                etag = (await reader.ReadElementContentAsStringAsync()).Trim();
            else if (reader.LocalName.StartsWith(ChecksumPrefix, StringComparison.Ordinal) && reader.LocalName.Length > ChecksumPrefix.Length)
            {
                checksumName = reader.LocalName[ChecksumPrefix.Length..];
                checksum = (await reader.ReadElementContentAsStringAsync()).Trim();
            }
            else
                await reader.SkipAsync();
        }

        await reader.ReadAsync(); // past </Part>
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber) && !string.IsNullOrEmpty(etag)
            ? new S3CompletePart(partNumber, etag.Trim('"'), checksumName, checksum)
            : null;
    }

    /// <summary>One <c>&lt;Object&gt;</c>; leaves the reader after it. Null when it has no key.</summary>
    private static async Task<S3DeleteEntry?> ReadObjectAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync();
            return null;
        }

        string? key = null, versionId = null;
        var depth = reader.Depth;
        await reader.ReadAsync();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == Key)
                key = await reader.ReadElementContentAsStringAsync();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == VersionId)
                versionId = await reader.ReadElementContentAsStringAsync();
            else
                await reader.SkipAsync();
        }

        await reader.ReadAsync(); // past </Object>
        return string.IsNullOrEmpty(key) ? null : new S3DeleteEntry(key, string.IsNullOrEmpty(versionId) ? null : versionId);
    }

    /// <summary>
    /// Reads a request document to its end, refusing anything above <paramref name="maxBytes"/>. Reading until the
    /// stream returns 0 also runs the final checks of a verifying body (hash, checksum, trailer), so integrity errors
    /// surface here, before the document is used. The bytes are returned so callers can check Content-MD5.
    /// </summary>
    public static async Task<Result<byte[]>> ReadBodyAsync(Stream body, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024) + 1];
        var total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return S3XmlErrors.MalformedXml;
            if (total == buffer.Length)
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maxBytes + 1));
        }

        return buffer.AsSpan(0, total).ToArray();
    }
}
