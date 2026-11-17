using TimeStore.Compat.Tests.Infrastructure;

namespace TimeStore.Compat.Tests;

/// <summary>s3cmd: its own SigV4 signer (not an AWS SDK), 5 MiB multipart chunks, metadata in x-amz-meta-*.</summary>
public sealed class S3cmdTests(CompatServer fixture) : IClassFixture<CompatServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<ClientResult> S3cmd(params string[] arguments) => Clients.S3cmdAsync(fixture, _ct, arguments);

    [Fact]
    public async Task Put_Info_Get_Sync_Delete_RemoveBucket()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("s3cmd");
        var large = fixture.WriteFile("s3cmd/up/large.bin", 12_000_000, seed: 21);
        fixture.WriteFile("s3cmd/up/sub/a.bin", 6, seed: 22);

        (await S3cmd("mb", $"s3://{bucket}")).ShouldSucceed("s3cmd mb");
        (await S3cmd("put", large, $"s3://{bucket}/large.bin", "--multipart-chunk-size-mb=5")).ShouldSucceed("s3cmd put");
        var info = (await S3cmd("info", $"s3://{bucket}/large.bin")).ShouldSucceed("s3cmd info");
        (await S3cmd("get", $"s3://{bucket}/large.bin", "s3cmd/down.bin", "--force")).ShouldSucceed("s3cmd get");
        (await S3cmd("sync", "s3cmd/up/", $"s3://{bucket}/synced/")).ShouldSucceed("s3cmd sync");

        Assert.Contains("x-amz-meta-s3cmd-attrs", info.Output); // s3cmd stores file attributes as user metadata
        Assert.Equal(File.ReadAllBytes(fixture.PathOf(large)), File.ReadAllBytes(fixture.PathOf("s3cmd/down.bin")));
        var listing = (await S3cmd("ls", "--recursive", $"s3://{bucket}")).ShouldSucceed("s3cmd ls").Output;
        Assert.Contains($"s3://{bucket}/synced/sub/a.bin", listing);

        (await S3cmd("del", "--recursive", "--force", $"s3://{bucket}")).ShouldSucceed("s3cmd del");
        (await S3cmd("rb", $"s3://{bucket}")).ShouldSucceed("s3cmd rb");
    }
}
