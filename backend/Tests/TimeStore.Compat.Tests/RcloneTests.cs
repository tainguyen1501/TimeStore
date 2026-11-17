using TimeStore.Compat.Tests.Infrastructure;

namespace TimeStore.Compat.Tests;

/// <summary>rclone (AWS SDK for Go v2), provider "Other": its own multipart, listings and hash checks.</summary>
public sealed class RcloneTests(CompatServer fixture) : IClassFixture<CompatServer>
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<ClientResult> Rclone(params string[] arguments) => Clients.RcloneAsync(fixture, _ct, arguments);

    [Fact]
    public async Task Copy_Check_CopyBack_AndPurge()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("rclone");
        fixture.WriteFile("rclone/up/small.bin", 1000, seed: 11);
        fixture.WriteFile("rclone/up/large.bin", 20_000_000, seed: 12); // multipart
        fixture.WriteFile("rclone/up/sub/a.bin", 6, seed: 13);

        (await Rclone("mkdir", $"ts:{bucket}")).ShouldSucceed("rclone mkdir");
        (await Rclone("copy", "rclone/up", $"ts:{bucket}/data")).ShouldSucceed("rclone copy up");
        var check = (await Rclone("check", "rclone/up", $"ts:{bucket}/data")).ShouldSucceed("rclone check");
        (await Rclone("copy", $"ts:{bucket}/data", "rclone/down")).ShouldSucceed("rclone copy down");

        Assert.Contains("0 differences found", check.Output);
        foreach (var file in new[] { "small.bin", "large.bin", "sub/a.bin" })
            Assert.Equal(File.ReadAllBytes(fixture.PathOf($"rclone/up/{file}")), File.ReadAllBytes(fixture.PathOf($"rclone/down/{file}")));
        var listing = (await Rclone("ls", $"ts:{bucket}")).ShouldSucceed("rclone ls").Output;
        Assert.Contains("20000000 data/large.bin", listing);

        (await Rclone("purge", $"ts:{bucket}")).ShouldSucceed("rclone purge");
        var buckets = (await Rclone("lsd", "ts:")).ShouldSucceed("rclone lsd").Output;
        Assert.DoesNotContain(bucket, buckets);
    }

    [Fact]
    public async Task CopyTo_AndMoveTo_AreServerSideCopies()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var bucket = CompatServer.NewBucket("rclonemv");
        var file = fixture.WriteFile("rclone-mv/a.bin", 40_000, seed: 32);
        (await Rclone("mkdir", $"ts:{bucket}")).ShouldSucceed("rclone mkdir");
        (await Rclone("copyto", file, $"ts:{bucket}/a.bin")).ShouldSucceed("rclone copyto up");

        (await Rclone("copyto", $"ts:{bucket}/a.bin", $"ts:{bucket}/copied.bin")).ShouldSucceed("rclone copyto server-side");
        (await Rclone("moveto", $"ts:{bucket}/copied.bin", $"ts:{bucket}/moved.bin")).ShouldSucceed("rclone moveto");
        (await Rclone("copyto", $"ts:{bucket}/moved.bin", "rclone-mv/down.bin")).ShouldSucceed("rclone copyto down");

        Assert.Equal(File.ReadAllBytes(fixture.PathOf(file)), File.ReadAllBytes(fixture.PathOf("rclone-mv/down.bin")));
        var listing = (await Rclone("lsf", $"ts:{bucket}")).ShouldSucceed("rclone lsf").Output;
        Assert.Contains("moved.bin", listing);
        Assert.DoesNotContain("copied.bin", listing);
    }
}
