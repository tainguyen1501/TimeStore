namespace TimeStore.Compat.Tests.Infrastructure;

/// <summary>
/// The pinned client versions. Bumping one is a reviewed change: the version that was probed is recorded in
/// <c>docs/research/s3-compatibility.md</c>.
/// </summary>
public static class CompatClients
{
    public const string EnabledVariable = "TIMESTORE_COMPAT";
    public const string AwsCliImage = "amazon/aws-cli:2.40.3";
    public const string RcloneImage = "rclone/rclone:1.76.0";
    public const string S3cmdPackage = "s3cmd==2.4.0";

    /// <summary>Containers reach the server on the host through this name (added explicitly for Linux daemons).</summary>
    public const string DockerHost = "host.docker.internal";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnabledVariable) == "1";

    public static string SkipReason => $"Compatibility tests are opt-in: set {EnabledVariable}=1 (needs Docker and uv).";
}
