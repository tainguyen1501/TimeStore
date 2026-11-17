using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using TimeStore.Compat.Tests.Infrastructure;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Compat.Tests.Ceph;

/// <summary>Outcome of one s3-tests test case, keyed <c>file::test</c> (e.g. <c>test_s3.py::test_bucket_list_empty</c>).</summary>
public sealed record CephCase(string Id, bool Passed, string? Failure);

/// <summary>
/// Runs a subset of ceph s3-tests (MIT; executed, never copied or committed) against a <see cref="TimeStoreServer"/>:
/// downloads the pinned commit into the scratch directory, creates the extra users as access keys, writes the
/// configuration, runs pytest through <c>uv run</c> (an ephemeral environment, nothing installed on the host) and
/// reads the JUnit report.
/// </summary>
public static class CephS3TestsRunner
{
    /// <summary>Pinned commit of https://github.com/ceph/s3-tests (probed 2026-11-16).</summary>
    public const string Commit = "5b3987116eca0b4b8fa57d6b10f3db57bab196b6";

    public const string Python = "3.13";

    public static readonly string[] Files = ["s3tests/functional/test_s3.py", "s3tests/functional/test_headers.py"];

    /// <summary>
    /// Markers of features TimeStore does not have (yet), and of behaviour that differs on Amazon S3 itself
    /// (<c>fails_on_aws</c>: Amazon S3 is the specification). Narrow this list as features land.
    /// </summary>
    public static readonly string[] ExcludedMarkers =
    [
        // "bucket_policy" runs since step 6d. "lifecycle" (configuration tests) runs since step 6c; expiration tests need RGW's debug clock and stay out.
        "fails_on_aws", "auth_aws2", "lifecycle_expiration", "lifecycle_transition", "cloud_transition",
        "cloud_restore", "encryption", "bucket_encryption", "sse_s3", "object_lock", "user_policy",
        "group_policy", "role_policy", "session_policy", "iam_account", "iam_cross_account", "iam_role", "iam_tenant",
        "iam_user", "abac_test", "s3select", "s3website", "s3website_routing_rules", "s3website_redirect_location", "sns",
        "tagging", "test_of_sts", "token_claims_trust_policy_test", "token_principal_tag_role_policy_test",
        "token_request_tag_trust_policy_test", "token_resource_tags_test", "token_role_tags_test", "token_tag_keys_test",
        "webidentity_test", "bucket_logging", "bucket_logging_cleanup", "fails_without_logging_rollover", "appendobject",
        "s3control", "versioning", "delete_marker", "storage_class", "group",
    ];

    private const string ArchiveUrl = "https://codeload.github.com/ceph/s3-tests/tar.gz/" + Commit;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    public static async Task<IReadOnlyList<CephCase>> RunAsync(CompatServer fixture, CancellationToken ct)
    {
        var root = Path.Combine(fixture.WorkDirectory, "ceph");
        var source = await DownloadAsync(root, ct);
        var config = Path.Combine(root, "s3tests.conf");
        await File.WriteAllTextAsync(config, await ConfigurationAsync(fixture.Server, ct), ct);
        var report = Path.Combine(root, "report.xml");

        var run = await ClientProcess.RunAsync(
            "uvx",
            [
                "--quiet", "--python", Python, "--with-requirements", Path.Combine(source, "requirements.txt"),
                "--from", "pytest", "pytest", "-p", "no:cacheprovider", "-q",
                "-m", $"not ({string.Join(" or ", ExcludedMarkers)})", $"--junitxml={report}", .. Files,
            ],
            new Dictionary<string, string> { ["S3TEST_CONF"] = config }, source, ct, RunTimeout);

        // pytest exits non-zero whenever a test fails, which is expected here; only a missing report is an error.
        Assert.True(File.Exists(report), $"s3-tests produced no report (exit {run.ExitCode}):\n{run.Output}");
        return Parse(XDocument.Load(report));
    }

    private static async Task<string> DownloadAsync(string root, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        using var http = new HttpClient();
        await using var archive = await http.GetStreamAsync(ArchiveUrl, ct);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, root, overwriteFiles: true, ct);
        return Path.Combine(root, $"s3-tests-{Commit}");
    }

    /// <summary>
    /// The three users s3-tests needs. TimeStore has no accounts yet: every access key sees every bucket, so tests
    /// about isolation between users fail (and stay off the expected list) until IAM exists. User ids are the access
    /// key ids because that is what TimeStore reports as the owner.
    /// </summary>
    private static async Task<string> ConfigurationAsync(TimeStoreServer server, CancellationToken ct)
    {
        using var admin = await server.AdminClientAsync();
        async Task<CreatedAccessKeyResponse> KeyAsync(string who)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest($"s3-tests {who}"), JsonSerializerOptions.Web, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreatedAccessKeyResponse>(JsonSerializerOptions.Web, ct))!;
        }

        // The alt user stays a full-scope key: s3-tests set up and clean up through it (ListBuckets, its own buckets),
        // which a policy-scope key may not do in a single-tenant store (step 6d, docs/research/s3-bucket-policy.md).
        var alt = await KeyAsync("alt");
        var tenant = await KeyAsync("tenant");
        static string User(string section, string key, string secret, string extra = "") =>
            $"[{section}]\ndisplay_name = {key}\nuser_id = {key}\nemail = {section.Replace(' ', '-')}@example.com\n"
            + $"access_key = {key}\nsecret_key = {secret}\n{extra}\n";

        return $"[DEFAULT]\nhost = 127.0.0.1\nport = {server.S3Port}\nis_secure = False\nssl_verify = False\n\n"
            + "[fixtures]\nbucket prefix = s3t-{random}-\niam name prefix = s3-tests-\niam path prefix = /s3-tests/\n\n"
            + User("s3 main", TimeStoreServer.AccessKey, TimeStoreServer.SecretKey, "api_name =\n")
            + User("s3 alt", alt.AccessKeyId, alt.SecretKey)
            + User("s3 tenant", tenant.AccessKeyId, tenant.SecretKey, "tenant = testx\n")
            + User("iam", TimeStoreServer.AccessKey, TimeStoreServer.SecretKey, "account_id = TS00000000000000000001\n")
            + User("iam root", TimeStoreServer.AccessKey, TimeStoreServer.SecretKey, "account_id = TS00000000000000000001\n")
            + User("iam alt root", alt.AccessKeyId, alt.SecretKey, "account_id = TS00000000000000000002\n");
    }

    private static List<CephCase> Parse(XDocument report)
    {
        var cases = new List<CephCase>();
        foreach (var test in report.Descendants("testcase"))
        {
            var file = ((string?)test.Attribute("classname") ?? string.Empty).Split('.') is [.., var module] ? module + ".py" : "?";
            var id = $"{file}::{(string?)test.Attribute("name")}";
            if (test.Element("skipped") is not null)
                continue;
            var failure = test.Element("failure") ?? test.Element("error");
            cases.Add(new CephCase(id, failure is null, failure is null ? null : FirstLine((string?)failure.Attribute("message"))));
        }

        return cases;
    }

    private static string FirstLine(string? message) =>
        message?.Split('\n', 2)[0] is { Length: > 0 } line ? line[..Math.Min(line.Length, 200)] : "(no message)";
}
