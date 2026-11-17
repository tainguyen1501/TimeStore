using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Compat.Tests.Infrastructure;

/// <summary>Each client invoked the way a user runs it, configured for the fixture's server and root key.</summary>
public static class Clients
{
    private const string AccessKeyVariable = "AWS_ACCESS_KEY_ID";
    private const string SecretKeyVariable = "AWS_SECRET_ACCESS_KEY";

    /// <summary><c>aws …</c> in the pinned image, with the work directory as the current directory.</summary>
    public static Task<ClientResult> AwsAsync(CompatServer fixture, CancellationToken ct, params string[] arguments) =>
        AwsAsync(fixture, ct, arguments, fixture.ContainerEndpoint);

    /// <summary><c>aws …</c> with another endpoint URL (e.g. presigning for the host's address).</summary>
    public static Task<ClientResult> AwsAsync(CompatServer fixture, CancellationToken ct, string[] arguments, string endpoint) =>
        DockerAsync(fixture, CompatClients.AwsCliImage,
            [AccessKeyVariable, SecretKeyVariable, "AWS_DEFAULT_REGION"],
            new Dictionary<string, string>
            {
                [AccessKeyVariable] = TimeStoreServer.AccessKey,
                [SecretKeyVariable] = TimeStoreServer.SecretKey,
                ["AWS_DEFAULT_REGION"] = TimeStoreServer.Region,
            },
            ["--endpoint-url", endpoint, .. arguments], ct);

    /// <summary><c>rclone …</c> in the pinned image with a remote named <c>ts</c> configured by environment.</summary>
    public static Task<ClientResult> RcloneAsync(CompatServer fixture, CancellationToken ct, params string[] arguments)
    {
        var environment = new Dictionary<string, string>
        {
            ["RCLONE_CONFIG_TS_TYPE"] = "s3",
            ["RCLONE_CONFIG_TS_PROVIDER"] = "Other",
            ["RCLONE_CONFIG_TS_ACCESS_KEY_ID"] = TimeStoreServer.AccessKey,
            ["RCLONE_CONFIG_TS_SECRET_ACCESS_KEY"] = TimeStoreServer.SecretKey,
            ["RCLONE_CONFIG_TS_ENDPOINT"] = fixture.ContainerEndpoint,
            ["RCLONE_CONFIG_TS_REGION"] = TimeStoreServer.Region,
        };
        return DockerAsync(fixture, CompatClients.RcloneImage, [.. environment.Keys], environment, arguments, ct);
    }

    /// <summary>
    /// <c>s3cmd …</c> through <c>uvx</c> on the host. s3cmd reads keys from its config file only, so the file is
    /// written into the work directory (deleted with it) instead of passing the secret on the command line.
    /// </summary>
    public static Task<ClientResult> S3cmdAsync(CompatServer fixture, CancellationToken ct, params string[] arguments)
    {
        var config = fixture.PathOf(".s3cfg");
        if (!File.Exists(config))
        {
            File.WriteAllText(config, $"""
                [default]
                access_key = {TimeStoreServer.AccessKey}
                secret_key = {TimeStoreServer.SecretKey}
                host_base = {fixture.HostEndpoint}
                host_bucket = {fixture.HostEndpoint}
                use_https = False
                signature_v2 = False
                bucket_location = {TimeStoreServer.Region}
                """);
        }

        return ClientProcess.RunAsync(
            "uvx", ["--quiet", CompatClients.S3cmdPackage, "-c", config, .. arguments],
            new Dictionary<string, string>(), fixture.WorkDirectory, ct);
    }

    /// <summary>
    /// <c>docker run</c> with the work directory at <c>/work</c>. Environment variables are passed by name only
    /// (<c>-e NAME</c>, value inherited from this process), so secrets never appear in the command line.
    /// </summary>
    private static Task<ClientResult> DockerAsync(
        CompatServer fixture, string image, IReadOnlyList<string> passThrough, IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var docker = new List<string>
        {
            "run", "--rm", $"--add-host={CompatClients.DockerHost}:host-gateway",
            "-v", $"{fixture.WorkDirectory}:/work", "-w", "/work",
        };
        foreach (var name in passThrough)
            docker.AddRange(["-e", name]);
        docker.Add(image);
        docker.AddRange(arguments);
        return ClientProcess.RunAsync("docker", docker, environment, null, ct);
    }
}
