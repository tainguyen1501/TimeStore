using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Http;

/// <summary>
/// A server that cannot start says why in one line and exits with code 1, without a stack trace. The master key is
/// configuration (TimeStore:MasterKey or TimeStore:MasterKeyFile): never created, and checked against the stored keys.
/// </summary>
public sealed class StartupTests
{
    private const string MasterKeyVariable = "TimeStore__MasterKey";
    private const string MasterKeyFileVariable = "TimeStore__MasterKeyFile";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInvalidConfiguration_IsOneLine_AndExitCodeOne()
    {
        var failure = await StartFailsAsync(new ConfiguredServer(environment => environment["TimeStore__S3__RootSecretKey"] = "short"));

        Assert.Contains("TimeStore cannot start: TimeStore:S3:RootSecretKey is required", failure);
    }

    [Theory]
    [InlineData("")]        // the data directory itself
    [InlineData("blobs")]   // where object data lives
    [InlineData("tmp/b")]   // inside the scratch folder emptied at startup
    public async Task ABackupDirectory_ThatIsNotAFolderOfItsOwn_IsRefused(string inData)
    {
        // The fixture's own data directory is in its environment already (Configure runs last).
        var failure = await StartFailsAsync(new ConfiguredServer(environment =>
            environment["TimeStore__Backup__Path"] = Path.Combine(environment["TimeStore__DataPath"]!, inData)));

        Assert.Contains("TimeStore:Backup:Path must be a folder of its own", failure);
    }

    [Fact]
    public async Task WithoutAMasterKey_TheServerDoesNotStart_AndCreatesNone()
    {
        var server = new ConfiguredServer(environment => environment.Remove(MasterKeyVariable));
        var dataPath = server.DataPath;

        var failure = await StartFailsAsync(server, afterFailure: () =>
            Assert.False(File.Exists(Path.Combine(dataPath, "master.key"))));

        Assert.Contains("TimeStore:MasterKey (or TimeStore:MasterKeyFile) is required", failure);
        Assert.Contains("openssl rand -base64 32", failure);
    }

    [Fact]
    public async Task AMasterKeyFileOfAnEarlierVersion_InTheDataDirectory_IsNamedInTheHint()
    {
        var failure = await StartFailsAsync(new ConfiguredServer(
            environment => environment.Remove(MasterKeyVariable),
            prepare: data => File.WriteAllBytes(Path.Combine(data, "master.key"), RandomNumberGenerator.GetBytes(32))));

        Assert.Contains("holds master.key from an earlier version", failure);
        Assert.Contains("base64 <", failure);
    }

    [Fact]
    public async Task BothAValueAndAFile_AreRefused()
    {
        var failure = await StartFailsAsync(new ConfiguredServer(environment => environment[MasterKeyFileVariable] = "/run/secrets/key"));

        Assert.Contains("set TimeStore:MasterKey or TimeStore:MasterKeyFile, not both", failure);
    }

    [Fact]
    public async Task AMasterKeyFileThatDoesNotExist_IsRefused_AndNotCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timestore-missing-{Guid.NewGuid():N}.key");

        var failure = await StartFailsAsync(new ConfiguredServer(environment =>
        {
            environment.Remove(MasterKeyVariable);
            environment[MasterKeyFileVariable] = path;
        }));

        Assert.Contains("does not exist", failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AMalformedMasterKey_IsRefused_WithoutQuotingIt()
    {
        var failure = await StartFailsAsync(new ConfiguredServer(environment => environment[MasterKeyVariable] = "not-a-key-at-all"));

        Assert.Contains("TimeStore:MasterKey must be 32 bytes written as Base64", failure);
        Assert.DoesNotContain("not-a-key-at-all", failure);
    }

    [Fact]
    public async Task AMasterKeyFile_WithTheKeyAsBase64_Starts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timestore-key-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, TimeStoreServer.MasterKey + "\n", _ct);
        try
        {
            await using var server = new ConfiguredServer(environment =>
            {
                environment.Remove(MasterKeyVariable);
                environment[MasterKeyFileVariable] = path;
            });

            await server.InitializeAsync();

            using var admin = await server.AdminClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/access-keys", _ct)).StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnotherMasterKey_ThanTheStoredKeysWereEncryptedWith_IsRefused()
    {
        string? masterKey = null; // the fixture's key first
        await using var server = new ConfiguredServer(environment =>
        {
            if (masterKey is not null)
                environment[MasterKeyVariable] = masterKey;
        });
        await server.InitializeAsync();
        using (var admin = await server.AdminClientAsync())
        {
            var created = await admin.PostAsJsonAsync("/api/v1/access-keys", new CreateAccessKeyRequest("app", "viewer"), _ct);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(_ct));
        }

        await server.KillAsync();
        masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(server.RestartAsync);

        Assert.Contains("exited with code 1:", failure.Message);
        Assert.Contains("the master key does not decrypt 1 of 1 stored access keys", failure.Message);
    }

    /// <param name="afterFailure">Checks of the data directory before the fixture removes it.</param>
    private static async Task<string> StartFailsAsync(TimeStoreServer server, Action? afterFailure = null)
    {
        await using (server)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.InitializeAsync());
            Assert.Contains("exited with code 1:", failure.Message);
            Assert.DoesNotContain("Unhandled exception", failure.Message);
            afterFailure?.Invoke();
            return failure.Message;
        }
    }

    /// <summary>A server with its environment changed last, and optionally files put into its data directory first.</summary>
    private sealed class ConfiguredServer(Action<IDictionary<string, string?>> configure, Action<string>? prepare = null)
        : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment) => configure(environment);

        protected override ValueTask PrepareDataAsync(string dataPath)
        {
            if (prepare is not null)
            {
                Directory.CreateDirectory(dataPath);
                prepare(dataPath);
            }

            return ValueTask.CompletedTask;
        }
    }
}
