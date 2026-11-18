using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;

namespace TimeStore.Server.Tests.Infrastructure;

/// <summary>
/// A real TimeStore server in a child process: free ports, a temporary data directory, its own root key, Production
/// environment. By default it runs the built <c>TimeStore.Server.dll</c>; set <c>TIMESTORE_SERVER_BINARY</c> to a
/// Native AOT binary to run the same tests against it. Tests talk to it over HTTP like any client.
/// </summary>
public class TimeStoreServer : IAsyncLifetime
{
    public const string AccessKey = "e2e-root";
    public const string SecretKey = "e2e-root-secret";
    public const string Region = "us-east-1";
    private const string BinaryVariable = "TIMESTORE_SERVER_BINARY";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "timestore-e2e", Guid.NewGuid().ToString("N"));
    private readonly StringBuilder _output = new();
    private Process? _process;

    /// <summary>
    /// Grace before released blobs are deleted. Short by default so tests that count blob files wait briefly
    /// (<see cref="WaitUntilAsync"/>); a fixture that needs the production behaviour overrides it.
    /// </summary>
    protected virtual int BlobDeleteDelaySeconds => 1;

    /// <summary>Extra settings for a fixture (environment variables of the server process, set last).</summary>
    protected virtual void Configure(IDictionary<string, string?> environment)
    {
    }

    /// <summary>Fills the data directory before the server starts (a fixture that restores a backup). Missing by default.</summary>
    protected virtual ValueTask PrepareDataAsync(string dataPath) => ValueTask.CompletedTask;

    /// <summary>
    /// Runs the server binary as a command (<c>restore ...</c>) to its end: the same binary the tests start (the built
    /// dll, or the AOT binary of <c>TIMESTORE_SERVER_BINARY</c>). Returns its exit code and its output.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunCommandAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var start = StartInfo();
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.WorkingDirectory = AppContext.BaseDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the command.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await output + await error);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or a few seconds pass (background work on the server).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
    }

    public int S3Port { get; private set; }

    public int AdminPort { get; private set; }

    public string S3Url => $"http://localhost:{S3Port}";

    public string AdminUrl => $"http://127.0.0.1:{AdminPort}";

    public string DataPath => _dataPath;

    /// <summary>
    /// An admin API client: same origin (<c>Origin</c> header, as a browser sends), its own cookie jar, signed in with
    /// the given key (the root key by default). The caller disposes it.
    /// </summary>
    public async Task<HttpClient> AdminClientAsync(string accessKey = AccessKey, string secretKey = SecretKey)
    {
        var client = AnonymousAdminClient();
        var response = await client.PostAsync("/api/v1/auth/login",
            new StringContent($"{{\"accessKeyId\":\"{accessKey}\",\"secretKey\":\"{secretKey}\"}}", Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Admin sign-in as {accessKey} failed: {(int)response.StatusCode}");
        return client;
    }

    /// <summary>An admin API client that is not signed in (same origin, own cookie jar).</summary>
    public HttpClient AnonymousAdminClient()
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
        {
            BaseAddress = new Uri(AdminUrl),
        };
        client.DefaultRequestHeaders.Add("Origin", AdminUrl);
        return client;
    }

    public AmazonS3Client Client(string accessKey = AccessKey, string secretKey = SecretKey) =>
        new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = S3Url,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            MaxErrorRetry = 0,
        });

    public async ValueTask InitializeAsync()
    {
        S3Port = FreePort();
        AdminPort = FreePort();
        await PrepareDataAsync(_dataPath);
        await StartProcessAsync();
    }

    /// <summary>
    /// Kills the server the way a crash or an OOM kill does (SIGKILL: no shutdown, nothing flushed or cleaned up) and
    /// keeps its data directory, so <see cref="RestartAsync"/> can start it again on the same data.
    /// </summary>
    public async Task KillAsync()
    {
        if (_process is null)
            return;
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        _process = null;
    }

    /// <summary>Starts the server again on the same data directory and ports, and waits until it is healthy.</summary>
    public Task RestartAsync() => _process is null
        ? StartProcessAsync()
        : throw new InvalidOperationException("The server is still running; kill it first.");

    /// <summary>What the server printed so far (all starts).</summary>
    public string Log => Output();

    private async Task StartProcessAsync()
    {
        var start = StartInfo();
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["TimeStore__S3Port"] = S3Port.ToString();
        start.Environment["TimeStore__AdminPort"] = AdminPort.ToString();
        start.Environment["TimeStore__DataPath"] = _dataPath;
        start.Environment["TimeStore__S3__RootAccessKey"] = AccessKey;
        start.Environment["TimeStore__S3__RootSecretKey"] = SecretKey;
        start.Environment["TimeStore__S3__Region"] = Region;
        start.Environment["TimeStore__Blobs__DeleteDelaySeconds"] = BlobDeleteDelaySeconds.ToString();
        Configure(start.Environment);
        start.WorkingDirectory = AppContext.BaseDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the server.");
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilHealthyAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private static ProcessStartInfo StartInfo()
    {
        if (Environment.GetEnvironmentVariable(BinaryVariable) is { Length: > 0 } binary)
            return new ProcessStartInfo(binary);

        var server = Path.Combine(AppContext.BaseDirectory, "TimeStore.Server.dll");
        var start = new ProcessStartInfo(DotnetHost());
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(server);
        return start;
    }

    /// <summary>The dotnet host that runs this test process (works when dotnet is not on PATH).</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;

        // <root>/shared/Microsoft.NETCore.App/<version>/ → <root>/dotnet
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(dotnet) ? dotnet : "dotnet";
    }

    private async Task WaitUntilHealthyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited)
                throw new InvalidOperationException($"The server exited with code {_process.ExitCode}:\n{Output()}");
            try
            {
                if ((await http.GetAsync($"{AdminUrl}/health/live")).StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The server did not become healthy in {StartTimeout}:\n{Output()}");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void Append(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.AppendLine(line);
    }

    private string Output()
    {
        lock (_output)
            return _output.ToString();
    }
}
