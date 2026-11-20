using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Replication;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Objects;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;

namespace TimeStore.Server.Replication;

/// <summary>
/// The small S3 client replication needs (PutObject, multipart upload, DeleteObject), signed with SigV4 in the
/// <c>Authorization</c> header, path-style (<c>endpoint/bucket/key</c>), every body sent as <c>UNSIGNED-PAYLOAD</c> with
/// its <c>Content-MD5</c> (docs/research/replication.md "S3 client inside TimeStore"). Native AOT: HttpClient and
/// XmlReader only, no SDK. Logs nothing: errors carry the destination's S3 error code, never a secret or a signature.
/// </summary>
internal sealed class S3ReplicationClient(ReplicationOptions options, TimeProvider clock) : IReplicationClient, IDisposable
{
    private const int MaxErrorBodyBytes = 64 * 1024;
    private const int MaxErrorLength = 300;
    private const string ContentType = "content-type";
    private const string Host = "host";
    private const string Uploads = "uploads";
    private const string UploadId = "uploadId";
    private const string PartNumber = "partNumber";
    private const string XmlContentType = "application/xml";
    private const string ErrorElement = "Error";
    private const string CodeElement = "Code";
    private const string UploadIdElement = "UploadId";
    private const string ETagElement = "ETag";
    private const string NoSuchBucket = "NoSuchBucket";
    private const string NoSuchKey = "NoSuchKey";
    private const string PermanentRedirect = "PermanentRedirect";
    private static readonly byte[] TestBody = "TimeStore replication test"u8.ToArray();

    // Codes Amazon S3 answers for a request that may succeed later (besides 5xx and 429).
    private static readonly string[] TemporaryCodes =
        ["SlowDown", "RequestTimeout", "RequestTimeTooSkewed", "InternalError", "ServiceUnavailable", "OperationAborted", "NoSuchUpload"];

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = false,
        UseCookies = false,
        // User metadata may hold non-ASCII text; TimeStore and the S3 servers read headers as UTF-8.
        RequestHeaderEncodingSelector = static (_, _) => Encoding.UTF8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // per request: ReplicationOptions.RequestTimeout
    };

    public void Dispose() => _http.Dispose();

    public async ValueTask<ReplicationOutcome> PutAsync(
        ReplicationDestination destination, StoredObject source, Func<BlobLocator, CancellationToken, ValueTask<Stream>> openBlob,
        CancellationToken ct)
    {
        return await GuardAsync(source.Data switch
        {
            SingleBlob single => PutSingleAsync(destination, source, single.Blob, openBlob, ct),
            MultipartBlobs multipart => PutMultipartAsync(destination, source, multipart, openBlob, ct),
        }, ct);
    }

    public async ValueTask<ReplicationOutcome> DeleteAsync(ReplicationDestination destination, string key, CancellationToken ct) =>
        await GuardAsync(DeleteObjectAsync(destination, key, ct), ct);

    public async ValueTask<ReplicationOutcome> TestAsync(ReplicationDestination destination, CancellationToken ct)
    {
        return await GuardAsync(TestCoreAsync(), ct);

        async Task<ReplicationOutcome> TestCoreAsync()
        {
            var key = IReplicationClient.TestKeyPrefix + Guid.CreateVersion7().ToString("N");
            var headers = ObjectHeaders(MediaTypeText, [], []);
            headers[S3HeaderNames.ContentMd5] = Convert.ToBase64String(MD5.HashData(TestBody));
            using (var put = await SendAsync(destination, HttpMethod.Put, key, string.Empty, headers, new ByteArrayContent(TestBody), TestBody.Length, ct))
            {
                if (await FailureOfAsync(put, ct) is { } failed)
                    return failed;
            }

            return await DeleteObjectAsync(destination, key, ct);
        }
    }

    private const string MediaTypeText = "text/plain";

    private async Task<ReplicationOutcome> PutSingleAsync(
        ReplicationDestination destination, StoredObject source, BlobLocator blob,
        Func<BlobLocator, CancellationToken, ValueTask<Stream>> openBlob, CancellationToken ct)
    {
        var headers = ObjectHeaders(source.ContentType, source.Headers, source.UserMetadata);
        headers[S3HeaderNames.ContentMd5] = Md5Base64(blob.Md5Hex);
        await using var body = await openBlob(blob, ct);
        using var response = await SendAsync(destination, HttpMethod.Put, source.Key, string.Empty, headers, new StreamContent(body), blob.Size, ct);
        if (await FailureOfAsync(response, ct) is { } failed)
            return failed;
        return SameETag(response.Headers.ETag?.Tag, blob.Md5Hex);
    }

    /// <summary>
    /// The source's own parts, one destination part each (they already meet the 5 MB minimum), each with its MD5: the
    /// destination's ETag then equals the source's <c>md5-N</c>. A failed upload is aborted.
    /// </summary>
    private async Task<ReplicationOutcome> PutMultipartAsync(
        ReplicationDestination destination, StoredObject source, MultipartBlobs multipart,
        Func<BlobLocator, CancellationToken, ValueTask<Stream>> openBlob, CancellationToken ct)
    {
        string uploadId;
        var create = ObjectHeaders(source.ContentType, source.Headers, source.UserMetadata);
        using (var created = await SendAsync(destination, HttpMethod.Post, source.Key, SigV4RequestSigner.Query((Uploads, string.Empty)), create, null, 0, ct))
        {
            if (await FailureOfAsync(created, ct) is { } failed)
                return failed;
            if (await ReadElementAsync(created, UploadIdElement, ct) is not { Length: > 0 } id)
                return new ReplicationOutcome(ReplicationFailure.Temporary, "CreateMultipartUpload answered no UploadId");
            uploadId = id;
        }

        var completed = false;
        try
        {
            var complete = new StringBuilder("<CompleteMultipartUpload>");
            for (var i = 0; i < multipart.Parts.Count; i++)
            {
                var part = multipart.Parts[i];
                var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [S3HeaderNames.ContentMd5] = Md5Base64(part.Blob.Md5Hex),
                };
                await using var body = await openBlob(part.Blob, ct);
                var query = SigV4RequestSigner.Query((PartNumber, number), (UploadId, uploadId));
                using var uploaded = await SendAsync(destination, HttpMethod.Put, source.Key, query, headers, new StreamContent(body), part.Blob.Size, ct);
                if (await FailureOfAsync(uploaded, ct) is { } failed)
                    return failed;
                complete.Append("<Part><PartNumber>").Append(number).Append("</PartNumber><ETag>\"")
                    .Append(part.Blob.Md5Hex).Append("\"</ETag></Part>");
            }

            var document = Encoding.UTF8.GetBytes(complete.Append("</CompleteMultipartUpload>").ToString());
            var completeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ContentType] = XmlContentType };
            using var done = await SendAsync(
                destination, HttpMethod.Post, source.Key, SigV4RequestSigner.Query((UploadId, uploadId)), completeHeaders,
                new ByteArrayContent(document), document.Length, ct);
            if (await FailureOfAsync(done, ct) is { } completeFailed)
                return completeFailed;
            // Amazon S3 may answer 200 with an <Error> document to a Complete that failed late.
            var (root, etag) = await ReadCompleteAsync(done, ct);
            if (root == ErrorElement)
                return new ReplicationOutcome(ReplicationFailure.Temporary, $"CompleteMultipartUpload failed: {etag}");
            completed = true;
            return SameETag(etag, source.ETag);
        }
        finally
        {
            if (!completed)
                await AbortQuietlyAsync(destination, source.Key, uploadId);
        }
    }

    private async Task AbortQuietlyAsync(ReplicationDestination destination, string key, string uploadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var _ = await SendAsync(
                destination, HttpMethod.Delete, key, SigV4RequestSigner.Query((UploadId, uploadId)),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, 0, timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // best effort: the destination's lifecycle removes abandoned uploads
        }
    }

    private async Task<ReplicationOutcome> DeleteObjectAsync(ReplicationDestination destination, string key, CancellationToken ct)
    {
        using var response = await SendAsync(
            destination, HttpMethod.Delete, key, string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, 0, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && await ReadElementAsync(response, CodeElement, ct) == NoSuchKey)
            return ReplicationOutcome.Success; // already gone
        return await FailureOfAsync(response, ct) ?? ReplicationOutcome.Success;
    }

    /// <summary>Signs and sends one request; the response is read by the caller.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        ReplicationDestination destination, HttpMethod method, string key, string query, Dictionary<string, string> headers,
        HttpContent? content, long length, CancellationToken ct)
    {
        var path = SigV4RequestSigner.EncodePath(destination.Bucket, key);
        var authority = destination.Endpoint.GetLeftPart(UriPartial.Authority);
        // Keys may hold "..", "//" or "%": the path must go out exactly as signed.
        var uri = new Uri(authority + path + (query.Length > 0 ? "?" + query : string.Empty),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        headers[Host] = destination.Endpoint.IsDefaultPort ? destination.Endpoint.Host : destination.Endpoint.Authority;
        var payload = content is null ? SigV4Names.EmptySha256 : SigV4Names.UnsignedPayload;
        var authorization = SigV4RequestSigner.Sign(
            method.Method, path, query, headers, payload, destination.AccessKeyId, destination.SecretKey, region: destination.Region,
            now: clock.GetUtcNow().UtcDateTime);

        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (content is not null)
            content.Headers.ContentLength = length;
        foreach (var (name, value) in headers)
        {
            if (name == Host)
                continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.TryAddWithoutValidation(HttpRequestHeader.Authorization.ToString(), authorization);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The destination did not answer within {options.RequestTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Turns every exception a send can raise into an outcome (shutdown excepted).</summary>
    private static async Task<ReplicationOutcome> GuardAsync(Task<ReplicationOutcome> send, CancellationToken ct)
    {
        try
        {
            return await send;
        }
        catch (ReplicationSourceException exception)
        {
            return new ReplicationOutcome(ReplicationFailure.SourceUnavailable, Shorten(exception.Message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException or XmlException
                                              || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new ReplicationOutcome(ReplicationFailure.Temporary, Shorten($"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    /// <summary>Null for a 2xx answer, else the outcome its status and S3 error code mean.</summary>
    private static async Task<ReplicationOutcome?> FailureOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return null;
        var code = await ReadElementAsync(response, CodeElement, ct) ?? response.ReasonPhrase ?? "Error";
        var error = Shorten($"{code} ({status})");
        if (status >= 500 || status is 408 or 429 || TemporaryCodes.Contains(code))
            return new ReplicationOutcome(ReplicationFailure.Temporary, error);
        if (status is 401 or 403 or 301 or 307 || code is NoSuchBucket or PermanentRedirect)
            return new ReplicationOutcome(ReplicationFailure.DestinationRejected, error);
        return new ReplicationOutcome(ReplicationFailure.ObjectRejected, error);
    }

    /// <summary>The destination's ETag must be the source's when it has the same form (hex MD5, or <c>md5-N</c>).</summary>
    private static ReplicationOutcome SameETag(string? returned, string expected)
    {
        var etag = returned?.Trim('"');
        if (string.IsNullOrEmpty(etag) || etag.Length < BlobLocator.Md5HexLength || etag.Contains('-') != expected.Contains('-')
            || string.Equals(etag, expected, StringComparison.OrdinalIgnoreCase))
            return ReplicationOutcome.Success; // a form we cannot compare (encrypted at the destination), or the same
        return new ReplicationOutcome(ReplicationFailure.ObjectRejected, $"ETag mismatch: the destination stored {etag}, expected {expected}");
    }

    private static Dictionary<string, string> ObjectHeaders(
        string contentType, IReadOnlyList<ObjectHeader> stored, IReadOnlyList<ObjectHeader> metadata)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ContentType] = contentType };
        foreach (var header in stored)
            headers[header.Name] = header.Value;
        foreach (var header in metadata)
            headers[S3HeaderNames.UserMetadataPrefix + header.Name] = header.Value;
        return headers;
    }

    private static string Md5Base64(string md5Hex) => Convert.ToBase64String(Convert.FromHexString(md5Hex));

    private static async Task<string?> ReadElementAsync(HttpResponseMessage response, string element, CancellationToken ct)
    {
        var document = await ReadSmallBodyAsync(response, ct);
        if (document.Length == 0)
            return null;
        using var reader = XmlReader.Create(new MemoryStream(document), Settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == element)
                return reader.ReadElementContentAsString();
        }

        return null;
    }

    /// <summary>The root element's name and, for a result, its ETag (for an error, its Code).</summary>
    private static async Task<(string? Root, string? Value)> ReadCompleteAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var document = await ReadSmallBodyAsync(response, ct);
        if (document.Length == 0)
            return (null, null);
        using var reader = XmlReader.Create(new MemoryStream(document), Settings);
        string? root = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            root ??= reader.LocalName;
            if (reader.LocalName == (root == ErrorElement ? CodeElement : ETagElement))
                return (root, reader.ReadElementContentAsString());
        }

        return (root, null);
    }

    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private static async Task<byte[]> ReadSmallBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length <= MaxErrorBodyBytes ? bytes : [];
    }

    private static string Shorten(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
