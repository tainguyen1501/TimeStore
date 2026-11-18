using TimeStore.Infra.Backups;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Server;

/// <summary>
/// <c>TimeStore.Server restore --archive &lt;file&gt; [--data-path &lt;dir&gt;] [--master-key &lt;file&gt;]</c>: restores a
/// backup into an empty data directory with the server stopped (docs/backup.md). Defaults come from the same
/// configuration as the server (<c>TimeStore:DataPath</c>, <c>TimeStore:MasterKeyFile</c>), so a restore Job can share
/// the server's environment.
/// </summary>
internal static class RestoreCommand
{
    public const string Name = "restore";
    private const string ArchiveOption = "--archive";
    private const string DataPathOption = "--data-path";
    private const string MasterKeyOption = "--master-key";
    private const int Success = 0;
    private const int Failure = 1;
    private const int Usage = 2;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == Name;

    public static async Task<int> RunAsync(string[] args, ServerOptions options)
    {
        string? archive = null;
        var dataPath = options.DataPath;
        string? masterKey = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
                return PrintUsage($"{args[i]} needs a value.");
            switch (args[i])
            {
                case ArchiveOption:
                    archive = args[++i];
                    break;
                case DataPathOption:
                    dataPath = args[++i];
                    break;
                case MasterKeyOption:
                    masterKey = args[++i];
                    break;
                default:
                    return PrintUsage($"Unknown option {args[i]}.");
            }
        }

        if (string.IsNullOrWhiteSpace(archive))
            return PrintUsage($"{ArchiveOption} is required.");
        if (!File.Exists(archive))
        {
            await Console.Error.WriteLineAsync($"The archive {archive} does not exist.");
            return Failure;
        }

        // The key goes where the server will look for it: --master-key, else the configured file, else the data path.
        masterKey ??= string.IsNullOrWhiteSpace(options.MasterKeyFile)
            ? Path.Combine(dataPath, ServerOptions.MasterKeyFileName)
            : options.MasterKeyFile;

        var restorer = new BackupRestorer(new TarBackupArchiveFormat());
        var result = await restorer.RestoreAsync(
            new RestoreRequest(archive, dataPath, masterKey, SqliteMetadataInitializer.LatestVersion), CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync($"Restore failed ({result.Error.Code}): {result.Error.Message}");
            return Failure;
        }

        var manifest = result.Value.Manifest;
        Console.WriteLine($"Restored {Path.GetFileName(archive)} into {Path.GetFullPath(dataPath)}:");
        Console.WriteLine($"  taken at {manifest.SnapshotAt:u} on {manifest.NodeId} (TimeStore {manifest.TimeStoreVersion}), {result.Value.Compression}");
        Console.WriteLine($"  {manifest.Buckets} buckets, {manifest.Objects} objects, {manifest.Versions} versions, {manifest.Blobs} blobs ({manifest.Bytes} bytes), all checksums verified");
        Console.WriteLine(result.Value.MasterKeyRestored
            ? $"  master key written to {Path.GetFullPath(masterKey)}"
            : $"  the archive holds no master key: the server needs the original one at {Path.GetFullPath(masterKey)}");
        return Success;
    }

    private static int PrintUsage(string problem)
    {
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine(
            $"Usage: TimeStore.Server {Name} {ArchiveOption} <file> [{DataPathOption} <empty directory>] [{MasterKeyOption} <file>]");
        return Usage;
    }
}
