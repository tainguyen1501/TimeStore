using System.Net;
using TimeStore.Domain.Blobs;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore</c> of the configuration. Bound by the source generator (AOT).</summary>
public sealed class ServerOptions
{
    public const string Section = "TimeStore";
    public const string DefaultNodeId = "node-1";
    public const string DefaultDataPath = "data";
    public const string MetadataFileName = "metadata.db";
    /// <summary>The key file earlier versions created inside the data directory (named in the upgrade hint).</summary>
    public const string LegacyMasterKeyFileName = "master.key";
    public const int DefaultS3Port = 9000;
    public const int DefaultAdminPort = 9001;

    /// <summary>Loopback by default; any address works since the admin API requires sign-in.</summary>
    public const string DefaultAdminAddress = "127.0.0.1";

    /// <summary>This node's id, recorded in every blob locator. Stable for the node's lifetime.</summary>
    public string NodeId { get; set; } = DefaultNodeId;

    /// <summary>Data directory: <c>metadata.db</c>, <c>blobs/</c>, <c>tmp/</c>.</summary>
    public string DataPath { get; set; } = DefaultDataPath;

    /// <summary>
    /// The master key that encrypts stored access-key secrets (not objects): 32 bytes as Base64
    /// (<c>openssl rand -base64 32</c>). Required, this or <see cref="MasterKeyFile"/>; TimeStore never creates one.
    /// Keep a copy apart from the data: without it every stored access key must be created again.
    /// </summary>
    public string? MasterKey { get; set; }

    /// <summary>A file holding the master key (Base64, or the 32 raw bytes): a mounted Secret, a Docker secret.</summary>
    public string? MasterKeyFile { get; set; }

    /// <summary>S3 API (hot path, own dispatcher), on every interface.</summary>
    public int S3Port { get; set; } = DefaultS3Port;

    /// <summary>Admin API (<c>/api/v1</c>) and, later, the React UI.</summary>
    public int AdminPort { get; set; } = DefaultAdminPort;

    /// <summary>
    /// Address the admin port binds to (loopback by default). The admin API requires sign-in; on a network address
    /// without a TLS proxy secrets travel in clear text, and the server logs a warning.
    /// </summary>
    public string AdminAddress { get; set; } = DefaultAdminAddress;

    /// <summary>S3 protocol settings (section <c>TimeStore:S3</c>).</summary>
    public S3Options S3 { get; set; } = new();

    /// <summary>Orphan-blob sweep (section <c>TimeStore:BlobSweep</c>); its interval also paces the maintenance worker.</summary>
    public BlobSweepSettings BlobSweep { get; set; } = new();

    /// <summary>Multipart uploads (section <c>TimeStore:Multipart</c>).</summary>
    public MultipartSettings Multipart { get; set; } = new();

    /// <summary>Admin API sign-in (section <c>TimeStore:Admin</c>).</summary>
    public AdminSettings Admin { get; set; } = new();

    /// <summary>Bucket lifecycle rules (section <c>TimeStore:Lifecycle</c>).</summary>
    public LifecycleSettings Lifecycle { get; set; } = new();

    /// <summary>Blob lifecycle (section <c>TimeStore:Blobs</c>).</summary>
    public BlobSettings Blobs { get; set; } = new();

    /// <summary>Backups (section <c>TimeStore:Backup</c>).</summary>
    public BackupSettings Backup { get; set; } = new();

    /// <summary>The first problem found, or null. Bad configuration must stop startup.</summary>
    public string? Validate()
    {
        if (S3.Validate() is { } s3Problem)
            return s3Problem;
        if (BlobSweep.Validate() is { } sweepProblem)
            return sweepProblem;
        if (Multipart.Validate() is { } multipartProblem)
            return multipartProblem;
        if (Admin.Validate() is { } adminProblem)
            return adminProblem;
        if (Blobs.Validate() is { } blobProblem)
            return blobProblem;
        if (Lifecycle.Validate() is { } lifecycleProblem)
            return lifecycleProblem;
        if (Backup.Validate(DataPath) is { } backupProblem)
            return backupProblem;
        if (!IsValidNodeId(NodeId))
            return $"{Section}:{nameof(NodeId)} must be 1-{BlobLocator.NodeIdMaxLength} lowercase letters, digits or hyphens.";
        if (string.IsNullOrWhiteSpace(DataPath))
            return $"{Section}:{nameof(DataPath)} is required.";
        if (MasterKeyProblem() is { } keyProblem)
            return keyProblem;
        if (S3Port == AdminPort)
            return $"{Section}:{nameof(S3Port)} and {Section}:{nameof(AdminPort)} must differ.";
        if (!IPAddress.TryParse(AdminAddress, out _))
            return $"{Section}:{nameof(AdminAddress)} must be an IP address.";
        return null;
    }

    private string? MasterKeyProblem()
    {
        var hasValue = !string.IsNullOrWhiteSpace(MasterKey);
        var hasFile = !string.IsNullOrWhiteSpace(MasterKeyFile);
        if (hasValue && hasFile)
            return $"set {Section}:{nameof(MasterKey)} or {Section}:{nameof(MasterKeyFile)}, not both.";
        if (hasValue || hasFile)
            return null;

        var problem = $"{Section}:{nameof(MasterKey)} (or {Section}:{nameof(MasterKeyFile)}) is required: "
            + "32 bytes as Base64, created with: openssl rand -base64 32.";
        var legacy = Path.Combine(DataPath, LegacyMasterKeyFileName);
        return File.Exists(legacy)
            ? problem + $" The data directory holds {LegacyMasterKeyFileName} from an earlier version: move it out of the "
                + $"data directory and set {Section}:{nameof(MasterKeyFile)} to it, or set {Section}:{nameof(MasterKey)} to "
                + $"its Base64 (base64 < {legacy})."
            : problem;
    }

    private static bool IsValidNodeId(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) || nodeId.Length > BlobLocator.NodeIdMaxLength)
            return false;
        foreach (var c in nodeId)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                return false;
        }

        return true;
    }
}
