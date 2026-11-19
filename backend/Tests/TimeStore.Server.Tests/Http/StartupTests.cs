using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Http;

/// <summary>A server that cannot start says why in one line and exits with code 1, without a stack trace.</summary>
public sealed class StartupTests
{
    [Fact]
    public async Task AnInvalidConfiguration_IsOneLine_AndExitCodeOne()
    {
        await using var server = new MisconfiguredServer();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.InitializeAsync());

        Assert.Contains("exited with code 1:", failure.Message);
        Assert.Contains("TimeStore cannot start: TimeStore:S3:RootSecretKey is required", failure.Message);
        Assert.DoesNotContain("Unhandled exception", failure.Message);
    }

    /// <summary>A root secret shorter than the minimum.</summary>
    private sealed class MisconfiguredServer : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment) =>
            environment["TimeStore__S3__RootSecretKey"] = "short";
    }
}
