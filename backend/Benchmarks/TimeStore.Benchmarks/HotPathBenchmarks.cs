using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using TimeStore.Cryptography.Checksums;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Xml;

namespace TimeStore.Benchmarks;

/// <summary>
/// The per-request work of the S3 port that is not I/O: signature verification, query canonicalization, chunked body
/// decoding and listing XML. Inputs are the AWS documentation example (signature) and synthetic payloads.
/// </summary>
[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const int ChunkSize = 64 * 1024;
    private const int BodySize = 1024 * 1024;

    private SigV4Verifier _verifier = null!;
    private Request _getObject = null!;
    private byte[] _chunkedBody = null!;
    private SigV4Principal _unsignedTrailer = null!;
    private byte[] _signedBody = null!;
    private SigV4Principal _signedChunks = null!;
    private readonly ChecksumFactory _checksums = new();
    private S3ObjectListing _listing = null!;
    private readonly ListObjectsV2 _listRequest = new("photos", "2026/", null, null, null, 1000, EncodeUrl: true, FetchOwner: false);
    private readonly byte[] _copyBuffer = new byte[ChunkSize];

    [GlobalSetup]
    public void Setup()
    {
        // AWS SigV4 documentation example: GET /test.txt with a Range header, 2013-05-24.
        var signedAt = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
        _verifier = new SigV4Verifier(new Secrets(), new SigningKeyCache(), new FixedClock(signedAt),
            new SigV4Options("us-east-1", TimeSpan.FromMinutes(15)));
        _getObject = new Request("GET", "/test.txt", new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z",
            ["authorization"] = $"AWS4-HMAC-SHA256 Credential={AccessKey}/20130524/us-east-1/s3/aws4_request,"
                + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
                + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
        });

        // 1 MiB in 64 KiB chunks, unsigned with a CRC32 trailer: what current SDKs send by default over HTTPS.
        var payload = new byte[BodySize];
        new Random(1).NextBytes(payload);
        using var crc = _checksums.Create(ChecksumAlgorithm.Crc32);
        crc.Append(payload);
        var digest = new byte[4];
        crc.GetHashAndReset(digest);
        var body = new MemoryStream();
        for (var offset = 0; offset < BodySize; offset += ChunkSize)
        {
            body.Write(Encoding.ASCII.GetBytes($"{ChunkSize:x}\r\n"));
            body.Write(payload, offset, ChunkSize);
            body.Write("\r\n"u8);
        }

        body.Write(Encoding.ASCII.GetBytes($"0\r\nx-amz-checksum-crc32:{Convert.ToBase64String(digest)}\r\n\r\n"));
        _chunkedBody = body.ToArray();
        _unsignedTrailer = new SigV4Principal(AccessKey, signedAt.UtcDateTime, "20130524T000000Z",
            "20130524/us-east-1/s3/aws4_request", new string('0', 64), new byte[32],
            PayloadMode.StreamingUnsignedTrailer, null, false);

        // The same payload with signed chunks (STREAMING-AWS4-HMAC-SHA256-PAYLOAD): what SDKs send over plain HTTP.
        const string timestamp = "20130524T000000Z";
        var scope = SigV4Canonical.Scope("20130524", "us-east-1");
        var signingKey = SigV4Signer.DeriveSigningKey(SecretKey, "20130524", "us-east-1");
        var seedSignature = new string('a', 64);
        _signedChunks = new SigV4Principal(AccessKey, signedAt.UtcDateTime, timestamp, scope, seedSignature, signingKey,
            PayloadMode.StreamingSigned, null, false);
        var signed = new MemoryStream();
        var previous = seedSignature;
        for (var offset = 0; offset <= BodySize; offset += ChunkSize)
        {
            var chunk = payload.AsSpan(offset, Math.Min(ChunkSize, BodySize - offset)); // the last pass is the 0-byte chunk
            var stringToSign = $"{SigV4Names.ChunkAlgorithm}\n{timestamp}\n{scope}\n{previous}\n{SigV4Names.EmptySha256}\n"
                + Convert.ToHexStringLower(SHA256.HashData(chunk));
            previous = Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
            signed.Write(Encoding.ASCII.GetBytes($"{chunk.Length:x};chunk-signature={previous}\r\n"));
            signed.Write(chunk);
            signed.Write("\r\n"u8);
        }

        _signedBody = signed.ToArray();

        var entries = new S3ObjectEntry[1000];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new S3ObjectEntry($"2026/11/16/photo-{i:D5} copy.jpg", signedAt.UtcDateTime, "9b2cf535f27731c974343645a3985328", 123_456, "STANDARD");
        _listing = new S3ObjectListing(entries, ["2026/11/17/"], true, "token");
    }

    [Benchmark(Description = "SigV4 header verify (GET)")]
    public async Task<bool> VerifyHeader() => (await _verifier.VerifyAsync(_getObject, CancellationToken.None)).IsSuccess;

    [Benchmark(Description = "Canonical query (presigned GET)")]
    public string? CanonicalQuery() => SigV4Canonical.CanonicalQuery(
        "X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
        + "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host&response-content-type=text%2Fplain"
        + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", SigV4Names.QuerySignature);

    [Benchmark(Description = "aws-chunked 1 MiB + CRC32 trailer")]
    public async Task<long> DecodeChunked()
    {
        await using var stream = new AwsChunkedStream(new MemoryStream(_chunkedBody, writable: false),
            new AwsChunkedSettings(PayloadMode.StreamingUnsignedTrailer, BodySize, ChecksumAlgorithm.Crc32, _unsignedTrailer), _checksums);
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(_copyBuffer)) > 0)
            total += read;
        return total;
    }

    [Benchmark(Description = "aws-chunked 1 MiB, signed chunks")]
    public async Task<long> DecodeSignedChunks()
    {
        await using var stream = new AwsChunkedStream(new MemoryStream(_signedBody, writable: false),
            new AwsChunkedSettings(PayloadMode.StreamingSigned, BodySize, null, _signedChunks), _checksums);
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(_copyBuffer)) > 0)
            total += read;
        return total;
    }

    [Benchmark(Description = "ListObjectsV2 XML, 1000 keys")]
    public Task ListXml() => S3XmlWriter.ListObjectsV2Async(Stream.Null, _listRequest, _listing, new S3Owner(AccessKey, AccessKey));

    private sealed class Secrets : ISecretKeyResolver
    {
        public ValueTask<string?> FindSecretKeyAsync(string accessKeyId, CancellationToken ct) =>
            ValueTask.FromResult<string?>(accessKeyId == AccessKey ? SecretKey : null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Request(string method, string rawTarget, Dictionary<string, string> headers) : ISigV4Request
    {
        public string Method => method;

        public string RawTarget => rawTarget;

        public IEnumerable<string> HeaderNames => headers.Keys;

        public string? GetHeader(string name) => headers.GetValueOrDefault(name);
    }
}
