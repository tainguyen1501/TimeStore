using System.Text.Json;
using TimeStore.Compat.Tests.Infrastructure;

namespace TimeStore.Compat.Tests;

/// <summary>AWS CLI v2 (botocore): flexible checksums on by default, CRC64NVME full-object for multipart.</summary>
public sealed class AwsCliTests(CompatServer fixture) : IClassFixture<CompatServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<ClientResult> Aws(params string[] arguments) => Clients.AwsAsync(fixture, _ct, arguments);

    [Fact]
    public async Task Copy_SmallAndMultipart_RoundTripsByteForByte_WithTheDefaultChecksums()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awscli");
        var small = fixture.WriteFile("aws-copy/small.bin", 1000, seed: 1);
        var large = fixture.WriteFile("aws-copy/large.bin", 20_000_000, seed: 2); // above the 8 MiB multipart threshold

        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");
        (await Aws("s3", "cp", small, $"s3://{bucket}/small.bin")).ShouldSucceed("aws s3 cp small");
        (await Aws("s3", "cp", large, $"s3://{bucket}/large.bin")).ShouldSucceed("aws s3 cp large");
        (await Aws("s3", "cp", $"s3://{bucket}/large.bin", "aws-copy/down/large.bin")).ShouldSucceed("aws s3 cp down");

        Assert.Equal(File.ReadAllBytes(fixture.PathOf(large)), File.ReadAllBytes(fixture.PathOf("aws-copy/down/large.bin")));
        var head = JsonDocument.Parse((await Aws("s3api", "head-object", "--bucket", bucket, "--key", "large.bin",
            "--checksum-mode", "ENABLED")).ShouldSucceed("aws s3api head-object").Output).RootElement;
        Assert.EndsWith("-3\"", head.GetProperty("ETag").GetString());
        Assert.Equal("FULL_OBJECT", head.GetProperty("ChecksumType").GetString());
        Assert.True(head.TryGetProperty("ChecksumCRC64NVME", out _), head.ToString());
    }

    [Fact]
    public async Task Sync_BothWays_AndASecondSyncUploadsNothing()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awssync");
        fixture.WriteFile("aws-sync/up/a.bin", 100, seed: 3);
        fixture.WriteFile("aws-sync/up/sub/b.bin", 200, seed: 4);
        fixture.WriteFile("aws-sync/up/sub/deeper/c.bin", 300, seed: 5);

        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");
        (await Aws("s3", "sync", "aws-sync/up", $"s3://{bucket}/data/")).ShouldSucceed("aws s3 sync up");
        var again = (await Aws("s3", "sync", "aws-sync/up", $"s3://{bucket}/data/")).ShouldSucceed("aws s3 sync again");
        (await Aws("s3", "sync", $"s3://{bucket}/data/", "aws-sync/down")).ShouldSucceed("aws s3 sync down");

        Assert.DoesNotContain("upload:", again.Output);
        foreach (var file in new[] { "a.bin", "sub/b.bin", "sub/deeper/c.bin" })
            Assert.Equal(File.ReadAllBytes(fixture.PathOf($"aws-sync/up/{file}")), File.ReadAllBytes(fixture.PathOf($"aws-sync/down/{file}")));
        var listing = (await Aws("s3", "ls", "--recursive", $"s3://{bucket}/")).ShouldSucceed("aws s3 ls").Output;
        Assert.Contains("data/sub/deeper/c.bin", listing);
    }

    [Fact]
    public async Task Presign_ProducesAUrlAPlainHttpClientCanFetch()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awspresign");
        var file = fixture.WriteFile("aws-presign/object.bin", 4096, seed: 6);
        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");
        (await Aws("s3", "cp", file, $"s3://{bucket}/object.bin")).ShouldSucceed("aws s3 cp");

        // Presigned for the host the test fetches from: the signature covers the Host header.
        var url = (await Clients.AwsAsync(fixture, _ct, ["s3", "presign", $"s3://{bucket}/object.bin", "--expires-in", "300"],
            endpoint: $"http://{fixture.HostEndpoint}")).ShouldSucceed("aws s3 presign").Output.Trim();
        using var http = new HttpClient();

        Assert.Equal(File.ReadAllBytes(fixture.PathOf(file)), await http.GetByteArrayAsync(url, _ct));
    }

    [Fact]
    public async Task DeletingEverything_ThenRemovingTheBucket()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awsrm");
        fixture.WriteFile("aws-rm/a.bin", 10, seed: 7);
        fixture.WriteFile("aws-rm/b/c.bin", 10, seed: 8);
        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");
        (await Aws("s3", "cp", "--recursive", "aws-rm", $"s3://{bucket}/")).ShouldSucceed("aws s3 cp --recursive");

        (await Aws("s3", "rm", "--recursive", $"s3://{bucket}/")).ShouldSucceed("aws s3 rm --recursive");
        (await Aws("s3", "rb", $"s3://{bucket}")).ShouldSucceed("aws s3 rb");
    }

    [Fact]
    public async Task Copy_AskingForEncryptionOrAnAcl_IsRefused_ButBucketOwnerFullControlIsFine()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awsrefuse");
        var file = fixture.WriteFile("aws-refuse/a.bin", 10, seed: 9);
        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");

        var encrypted = await Aws("s3", "cp", file, $"s3://{bucket}/sse.bin", "--sse", "AES256");
        var shared = await Aws("s3", "cp", file, $"s3://{bucket}/public.bin", "--acl", "public-read");
        var @private = await Aws("s3", "cp", file, $"s3://{bucket}/private.bin", "--acl", "private");
        (await Aws("s3", "cp", file, $"s3://{bucket}/owner.bin", "--acl", "bucket-owner-full-control"))
            .ShouldSucceed("aws s3 cp --acl bucket-owner-full-control");

        Assert.NotEqual(0, encrypted.ExitCode);
        Assert.Contains("NotImplemented", encrypted.Output);
        Assert.NotEqual(0, shared.ExitCode);
        Assert.Contains("AccessControlListNotSupported", shared.Output);
        Assert.NotEqual(0, @private.ExitCode); // Amazon S3 with ACLs disabled refuses it too
        Assert.Contains("AccessControlListNotSupported", @private.Output);
        var listing = (await Aws("s3", "ls", $"s3://{bucket}/")).ShouldSucceed("aws s3 ls").Output;
        Assert.Contains("owner.bin", listing);
        Assert.DoesNotContain("private.bin", listing);
        Assert.DoesNotContain("sse.bin", listing);
        Assert.DoesNotContain("public.bin", listing);
    }

    [Fact]
    public async Task Move_AndCopyBetweenBuckets_UseCopyObject()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("awsmv");
        var other = CompatServer.NewBucket("awsmv2");
        var file = fixture.WriteFile("aws-mv/a.bin", 50_000, seed: 31);
        (await Aws("s3", "mb", $"s3://{bucket}")).ShouldSucceed("aws s3 mb");
        (await Aws("s3", "mb", $"s3://{other}")).ShouldSucceed("aws s3 mb other");
        (await Aws("s3", "cp", file, $"s3://{bucket}/a.bin")).ShouldSucceed("aws s3 cp");

        (await Aws("s3", "mv", $"s3://{bucket}/a.bin", $"s3://{bucket}/renamed/a.bin")).ShouldSucceed("aws s3 mv");
        (await Aws("s3", "cp", $"s3://{bucket}/renamed/a.bin", $"s3://{other}/copied.bin")).ShouldSucceed("aws s3 cp s3→s3");
        (await Aws("s3", "cp", $"s3://{other}/copied.bin", "aws-mv/down.bin")).ShouldSucceed("aws s3 cp down");

        Assert.Equal(File.ReadAllBytes(fixture.PathOf(file)), File.ReadAllBytes(fixture.PathOf("aws-mv/down.bin")));
        var listing = (await Aws("s3", "ls", "--recursive", $"s3://{bucket}/")).ShouldSucceed("aws s3 ls").Output;
        Assert.Contains("renamed/a.bin", listing);
        Assert.DoesNotContain(" a.bin", listing.Replace("renamed/a.bin", ""));
    }
}
