using System.Formats.Tar;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TimeStore.Compat.Tests.Infrastructure;
using TimeStore.Protocol.Auth;

namespace TimeStore.Compat.Tests.SigV4;

/// <summary>
/// The official SigV4 test suite (aws-c-auth <c>tests/aws-signing-test-suite/v4</c>, Apache-2.0; downloaded at test
/// time, never committed). The suite signs for a generic service, so only the cases whose rules S3 shares are run:
/// every <c>normalize: false</c> case (S3 never normalizes the path) and the normalized cases whose path normalization
/// changes nothing; the query form of <c>omit_session_token</c> cases is left out (S3 signs the token). For each case, in both header and query (presigned) form, TimeStore's own functions must produce
/// the published canonical request, string to sign and signature.
/// </summary>
public sealed class SigV4TestSuiteTests(ITestOutputHelper output)
{
    /// <summary>Pinned commit of https://github.com/awslabs/aws-c-auth (last change to the suite: 2024-09-05).</summary>
    public const string Commit = "3281f8692e6fd10562c4585a4dded5c16b322698";

    private const string ArchiveUrl = "https://codeload.github.com/awslabs/aws-c-auth/tar.gz/" + Commit;
    private const string SuitePath = "/tests/aws-signing-test-suite/v4/";
    private const string SignatureParameter = "X-Amz-Signature";
    private const string SignedHeadersParameter = "X-Amz-SignedHeaders";
    private const string DateParameter = "X-Amz-Date";

    /// <summary>Fewer applicable cases than this means the suite layout changed: fail rather than pass on nothing.</summary>
    private const int MinimumCases = 25;

    [Fact]
    public async Task ApplicableCases_ProduceThePublishedCanonicalRequestStringToSignAndSignature()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var root = Path.Combine(Path.GetTempPath(), "timestore-sigv4-suite", Guid.NewGuid().ToString("N"));
        try
        {
            var cases = await DownloadAsync(root, TestContext.Current.CancellationToken);
            var failures = new List<string>();
            var run = 0;
            foreach (var directory in cases.OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                var context = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "context.json"))).RootElement;
                var request = RawRequest.Parse(File.ReadAllText(Path.Combine(directory, "request.txt")));
                if (context.GetProperty("normalize").GetBoolean() && NeedsNormalization(request.RawPath))
                {
                    output.WriteLine($"skipped {name}: path normalization (S3 never normalizes)");
                    continue;
                }

                run++;
                failures.AddRange(Check(name, "header", directory, context));

                // omit_session_token: the token is added to the query after signing (some services accept that);
                // S3 signs every query parameter, the token included (post-sts-header-before covers that form).
                if (context.TryGetProperty("omit_session_token", out var omit) && omit.GetBoolean())
                    output.WriteLine($"skipped {name} (query): session token outside the signature (S3 signs it)");
                else
                    failures.AddRange(Check(name, "query", directory, context));
            }

            output.WriteLine($"SigV4 suite {Commit[..7]}: {run} cases checked, {failures.Count} mismatches.");
            Assert.True(run >= MinimumCases, $"Only {run} applicable cases found: did the suite layout change?");
            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Canonical request, string to sign and signature of one case in one form, built by TimeStore's code.</summary>
    private static IEnumerable<string> Check(string name, string form, string directory, JsonElement context)
    {
        var signed = RawRequest.Parse(File.ReadAllText(Path.Combine(directory, $"{form}-signed-request.txt")));
        var expectedCanonical = File.ReadAllText(Path.Combine(directory, $"{form}-canonical-request.txt"));
        var expectedStringToSign = File.ReadAllText(Path.Combine(directory, $"{form}-string-to-sign.txt"));
        var expectedSignature = File.ReadAllText(Path.Combine(directory, $"{form}-signature.txt")).Trim();

        var query = form == "query";
        var signedHeaders = query
            ? Uri.UnescapeDataString(signed.QueryValue(SignedHeadersParameter)!).Split(';')
            : AuthorizationPart(signed.Header("authorization")!, "SignedHeaders").Split(';');
        var timestamp = query ? signed.QueryValue(DateParameter)! : signed.Header("x-amz-date")!;
        var region = context.GetProperty("region").GetString()!;
        var service = context.GetProperty("service").GetString()!;
        var secret = context.GetProperty("credentials").GetProperty("secret_access_key").GetString()!;

        var canonical = SigV4Canonical.CanonicalRequest(
            signed.Method,
            SigV4Canonical.CanonicalUri(signed.RawPath) ?? "(undecodable path)",
            SigV4Canonical.CanonicalQuery(signed.RawQuery, query ? SignatureParameter : null) ?? "(undecodable query)",
            signed, signedHeaders, Convert.ToHexStringLower(SHA256.HashData(signed.Body)));
        if (canonical != expectedCanonical)
            yield return $"{name} ({form}): canonical request\n--- expected\n{expectedCanonical}\n--- TimeStore\n{canonical}";

        var stringToSign = SigV4Canonical.StringToSign(
            timestamp, SigV4Canonical.Scope(timestamp[..8], region, service), SigV4Signer.Sha256Hex(canonical));
        if (stringToSign != expectedStringToSign)
            yield return $"{name} ({form}): string to sign\n--- expected\n{expectedStringToSign}\n--- TimeStore\n{stringToSign}";

        Span<byte> signature = stackalloc byte[32];
        SigV4Signer.Sign(SigV4Signer.DeriveSigningKey(secret, timestamp[..8], region, service), stringToSign, signature);
        if (!SigV4Signer.Matches(expectedSignature, signature))
            yield return $"{name} ({form}): signature {Convert.ToHexStringLower(signature)} instead of {expectedSignature}";
    }

    /// <summary>Whether a generic-service normalizer would change the path (empty, dot segments, repeated slashes).</summary>
    private static bool NeedsNormalization(string rawPath) =>
        rawPath.Contains("//", StringComparison.Ordinal)
        || rawPath.Split('/').Any(segment => segment is "." or "..");

    private static string AuthorizationPart(string authorization, string part)
    {
        foreach (var piece in authorization.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece.StartsWith(part + "=", StringComparison.Ordinal))
                return piece[(part.Length + 1)..];
        }

        throw new InvalidDataException($"No {part} in '{authorization}'.");
    }

    private static async Task<List<string>> DownloadAsync(string root, CancellationToken ct)
    {
        using var http = new HttpClient();
        await using var archive = await http.GetStreamAsync(ArchiveUrl, ct);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        var cases = new HashSet<string>(StringComparer.Ordinal);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var index = entry.Name.IndexOf(SuitePath, StringComparison.Ordinal);
            if (entry.EntryType != TarEntryType.RegularFile || index < 0 || entry.DataStream is null)
                continue;
            var relative = entry.Name[(index + SuitePath.Length)..]; // "<case>/<file>"
            if (relative.Split('/') is not [var caseName, var file] || caseName.Contains("..") || file.Contains(".."))
                continue;
            var directory = Path.Combine(root, caseName);
            Directory.CreateDirectory(directory);
            await using var target = File.Create(Path.Combine(directory, file));
            await entry.DataStream.CopyToAsync(target, ct);
            cases.Add(directory);
        }

        return [.. cases];
    }

    /// <summary>
    /// A request file of the suite: request line, <c>Name:value</c> headers (repeated names joined with a comma,
    /// continuation lines appended, as an HTTP server presents them), blank line, body.
    /// </summary>
    private sealed class RawRequest : ISigV4Request
    {
        private const string HttpVersionSuffix = " HTTP/1.1";
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public required string Method { get; init; }

        public required string RawTarget { get; init; }

        public required byte[] Body { get; init; }

        public string RawPath => RawTarget.Split('?', 2)[0];

        public string RawQuery => RawTarget.Split('?', 2) is [_, var query] ? query : string.Empty;

        public IEnumerable<string> HeaderNames => _headers.Keys.Select(name => name.ToLowerInvariant());

        public string? GetHeader(string name) => _headers.GetValueOrDefault(name);

        public string? Header(string name) => GetHeader(name);

        public string? QueryValue(string name)
        {
            foreach (var pair in RawQuery.Split('&'))
            {
                if (pair.StartsWith(name + "=", StringComparison.Ordinal))
                    return pair[(name.Length + 1)..];
            }

            return null;
        }

        public static RawRequest Parse(string text)
        {
            var normalized = text.Replace("\r\n", "\n");
            var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
            var head = split < 0 ? normalized : normalized[..split];
            var body = split < 0 ? string.Empty : normalized[(split + 2)..];
            var lines = head.Split('\n');
            var requestLine = lines[0];
            var target = requestLine[(requestLine.IndexOf(' ') + 1)..];
            if (target.EndsWith(HttpVersionSuffix, StringComparison.Ordinal))
                target = target[..^HttpVersionSuffix.Length];

            var request = new RawRequest
            {
                Method = requestLine[..requestLine.IndexOf(' ')],
                RawTarget = target,
                Body = Encoding.UTF8.GetBytes(body),
            };
            string? last = null;
            foreach (var line in lines.Skip(1))
            {
                if (line.Length > 0 && char.IsWhiteSpace(line[0]) && last is not null)
                {
                    request._headers[last] += " " + line.Trim(); // obsolete line folding
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line[..colon];
                var value = line[(colon + 1)..];
                request._headers[name] = request._headers.TryGetValue(name, out var existing) ? existing + "," + value : value;
                last = name;
            }

            return request;
        }
    }
}
