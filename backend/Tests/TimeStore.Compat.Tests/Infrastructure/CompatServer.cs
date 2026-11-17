using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Compat.Tests.Infrastructure;

/// <summary>
/// A <see cref="TimeStoreServer"/> plus a scratch directory shared with the client containers. Starts nothing when
/// the compatibility tests are not enabled; when they are, Docker and uv must be present (a missing tool fails the run
/// instead of skipping it: the run was asked for).
/// </summary>
public sealed class CompatServer : IAsyncLifetime
{
    private TimeStoreServer? _server;

    public TimeStoreServer Server => _server ?? throw new InvalidOperationException(CompatClients.SkipReason);

    /// <summary>Mounted at <c>/work</c> in containers. Under the temp directory, which Docker Desktop shares by default.</summary>
    public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "timestore-compat", Guid.NewGuid().ToString("N"));

    /// <summary>The S3 endpoint as a container sees it.</summary>
    public string ContainerEndpoint => $"http://{CompatClients.DockerHost}:{Server.S3Port}";

    /// <summary>The S3 endpoint as a process on the host sees it.</summary>
    public string HostEndpoint => $"127.0.0.1:{Server.S3Port}";

    public async ValueTask InitializeAsync()
    {
        if (!CompatClients.Enabled)
            return;

        foreach (var tool in new[] { "docker", "uvx" })
        {
            var found = await ClientProcess.RunAsync(tool, ["--version"], new Dictionary<string, string>(), null, CancellationToken.None);
            found.ShouldSucceed($"{tool} (required by {CompatClients.EnabledVariable}=1)");
        }

        Directory.CreateDirectory(WorkDirectory);
        _server = new TimeStoreServer();
        await _server.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        if (Directory.Exists(WorkDirectory))
            Directory.Delete(WorkDirectory, recursive: true);
    }

    /// <summary>Random bytes written under <see cref="WorkDirectory"/>; returns the relative path.</summary>
    public string WriteFile(string relativePath, int size, int seed)
    {
        var path = Path.Combine(WorkDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return relativePath;
    }

    public string PathOf(string relativePath) => Path.Combine(WorkDirectory, relativePath);

    /// <summary>A bucket name no other test uses.</summary>
    public static string NewBucket(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
