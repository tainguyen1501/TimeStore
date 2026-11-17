using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Protocol.Auth;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>DeleteObject and DeleteObjects end to end.</summary>
public sealed class DeleteObjectTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"e2e-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task Put(string key) =>
        _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = key }, _ct);

    private async Task<bool> Exists(string key)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, key, _ct);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    [Fact]
    public async Task DeleteObject_RemovesIt_AndRepeatingIsStill204()
    {
        await Put("a.txt");

        var first = await _s3.DeleteObjectAsync(_bucket, "a.txt", _ct);
        var second = await _s3.DeleteObjectAsync(_bucket, "a.txt", _ct);

        Assert.Equal(HttpStatusCode.NoContent, first.HttpStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.HttpStatusCode);
        Assert.False(await Exists("a.txt"));
    }

    [Fact]
    public async Task DeleteObjects_ReportsEveryKey_AndRemovesThem()
    {
        await Put("x/1");
        await Put("x/2 with space");
        await Put("keep");

        var response = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = _bucket,
            Objects = [new KeyVersion { Key = "x/1" }, new KeyVersion { Key = "x/2 with space" }, new KeyVersion { Key = "missing" }],
        }, _ct);

        Assert.Equal(new[] { "missing", "x/1", "x/2 with space" }, response.DeletedObjects.Select(d => d.Key).Order());
        Assert.Empty(response.DeleteErrors ?? []);
        Assert.False(await Exists("x/1"));
        Assert.True(await Exists("keep"));
    }

    [Fact]
    public async Task DeleteObjects_Quiet_ReportsNothingOnSuccess()
    {
        await Put("q");

        var response = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = _bucket, Quiet = true, Objects = [new KeyVersion { Key = "q" }],
        }, _ct);

        Assert.Empty(response.DeletedObjects ?? []);
        Assert.False(await Exists("q"));
    }

    [Fact]
    public async Task DeleteObjects_ThenDeleteBucket_Succeeds()
    {
        await Put("only");

        await _s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = [new KeyVersion { Key = "only" }] }, _ct);
        var deleted = await _s3.DeleteBucketAsync(_bucket, _ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.HttpStatusCode);
        await _s3.PutBucketAsync(_bucket, _ct); // keep the fixture bucket for other tests
    }

    [Fact]
    public async Task DeleteObjects_WithoutContentMd5OrChecksum_IsInvalidRequest()
    {
        // The SDK always protects this request, so sign it by hand: UNSIGNED-PAYLOAD, no Content-MD5, no checksum.
        var body = "<Delete><Object><Key>a</Key></Object></Delete>"u8.ToArray();
        using var http = new HttpClient();

        var response = await http.SendAsync(SignedPost($"/{_bucket}?delete", body), _ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<Code>InvalidRequest</Code>", await response.Content.ReadAsStringAsync(_ct));
    }

    private HttpRequestMessage SignedPost(string target, byte[] body)
    {
        var now = DateTime.UtcNow;
        var timestamp = now.ToString(SigV4Names.TimestampFormat, CultureInfo.InvariantCulture);
        var date = now.ToString(SigV4Names.DateFormat, CultureInfo.InvariantCulture);
        var host = $"localhost:{server.S3Port}";
        var headers = new Dictionary<string, string>
        {
            ["host"] = host, ["x-amz-content-sha256"] = SigV4Names.UnsignedPayload, ["x-amz-date"] = timestamp,
        };
        string[] signed = ["host", "x-amz-content-sha256", "x-amz-date"];

        var queryStart = target.IndexOf('?');
        var canonical = SigV4Canonical.CanonicalRequest(
            "POST", SigV4Canonical.CanonicalUri(target.AsSpan(0, queryStart))!, SigV4Canonical.CanonicalQuery(target.AsSpan(queryStart + 1), null)!,
            new HeaderSource(headers), signed, SigV4Names.UnsignedPayload);
        var scope = SigV4Canonical.Scope(date, TimeStoreServer.Region);
        var key = SigV4Signer.DeriveSigningKey(TimeStoreServer.SecretKey, date, TimeStoreServer.Region);
        var signature = new byte[32];
        SigV4Signer.Sign(key, SigV4Canonical.StringToSign(timestamp, scope, SigV4Signer.Sha256Hex(canonical)), signature);

        var message = new HttpRequestMessage(HttpMethod.Post, $"{server.S3Url}{target}") { Content = new ByteArrayContent(body) };
        message.Headers.TryAddWithoutValidation("x-amz-content-sha256", SigV4Names.UnsignedPayload);
        message.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        message.Headers.TryAddWithoutValidation("Authorization",
            $"{SigV4Names.Algorithm} Credential={TimeStoreServer.AccessKey}/{scope},SignedHeaders={string.Join(';', signed)},"
            + $"Signature={Convert.ToHexStringLower(signature)}");
        return message;
    }

    private sealed class HeaderSource(Dictionary<string, string> headers) : ISigV4Request
    {
        public string Method => "POST";

        public string RawTarget => string.Empty;

        public IEnumerable<string> HeaderNames => headers.Keys;

        public string? GetHeader(string name) => headers.GetValueOrDefault(name);
    }
}
