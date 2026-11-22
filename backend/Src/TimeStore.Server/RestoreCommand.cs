using TimeStore.Blob.FileSystem;
using TimeStore.Infra.Backups;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Server;

/// <summary>
/// <c>TimeStore.Server restore --archive &lt;file or URL&gt; [--data-path &lt;dir&gt;] [--if-empty]</c>: restores a full
/// backup into an empty data directory with the server stopped (docs/backup.md). With <c>--if-empty</c> a directory
/// that already holds data is left alone (exit code 0): what the Helm chart's init container runs at every start for
/// <c>restore.archive</c>.
/// <para>
/// <c>restore --metadata --archive &lt;file or URL&gt; [--data-path &lt;dir&gt;] [--once] [--allow-missing]</c>: puts the
/// metadata of a metadata-only backup back onto the data directory it came from (docs/research/blob-retention.md). The
/// server must be stopped; every blob the backup references is checked first, and nothing changes when some are gone.
/// With <c>--once</c> an archive already restored there is skipped (the chart's <c>restore.metadataArchive</c>).
/// </para>
/// The data path defaults to the server's (<c>TimeStore:DataPath</c>). The master key is not part of an archive: the
/// restored server uses the key of its configuration.
/// </summary>
internal static class RestoreCommand
{
    public const string Name = "restore";
    private const string ArchiveOption = "--archive";
    private const string DataPathOption = "--data-path";
    private const string IfEmptyOption = "--if-empty";
    private const string MetadataOption = "--metadata";
    private const string OnceOption = "--once";
    private const string AllowMissingOption = "--allow-missing";
    private const string SetUpAgain =
        "set them up again (bucket → ⋯ → Replication) with \"Copy the objects already in the bucket too\": it copies every object again";
    private const int Success = 0;
    private const int Failure = 1;
    private const int Usage = 2;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == Name;

    public static async Task<int> RunAsync(string[] args, ServerOptions options)
    {
        string? archive = null;
        var dataPath = options.DataPath;
        bool ifEmpty = false, metadata = false, once = false, allowMissing = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case IfEmptyOption:
                    ifEmpty = true;
                    continue;
                case MetadataOption:
                    metadata = true;
                    continue;
                case OnceOption:
                    once = true;
                    continue;
                case AllowMissingOption:
                    allowMissing = true;
                    continue;
            }

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
                default:
                    return PrintUsage($"Unknown option {args[i]}.");
            }
        }

        if (string.IsNullOrWhiteSpace(archive))
            return PrintUsage($"{ArchiveOption} is required.");
        if (metadata && ifEmpty)
            return PrintUsage($"{IfEmptyOption} is for full restores; a metadata restore uses {OnceOption}.");
        if (!metadata && (once || allowMissing))
            return PrintUsage($"{OnceOption} and {AllowMissingOption} need {MetadataOption}.");

        return metadata
            ? await RestoreMetadataAsync(archive, dataPath, options, once, allowMissing)
            : await RestoreFullAsync(archive, dataPath, ifEmpty);
    }

    private static async Task<int> RestoreFullAsync(string archive, string dataPath, bool ifEmpty)
    {
        var restorer = new BackupRestorer(new TarBackupArchiveFormat());
        var result = await restorer.RestoreAsync(
            new RestoreRequest(archive, dataPath, SqliteMetadataInitializer.LatestVersion, ifEmpty), CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync($"Restore failed ({result.Error.Code}): {result.Error.Message}");
            return Failure;
        }

        if (result.Value.WasSkipped)
        {
            Console.WriteLine($"Restore skipped: {Path.GetFullPath(dataPath)} already holds data ({IfEmptyOption}).");
            return Success;
        }

        var manifest = result.Value.Manifest!;
        Console.WriteLine($"Restored {ArchiveSource.DisplayName(archive)} into {Path.GetFullPath(dataPath)}:");
        Console.WriteLine($"  taken at {manifest.SnapshotAt:u} on {manifest.NodeId} (TimeStore {manifest.TimeStoreVersion}), {result.Value.Compression}");
        Console.WriteLine($"  {manifest.Buckets} buckets, {manifest.Objects} objects, {manifest.Versions} versions, {manifest.Blobs} blobs ({manifest.Bytes} bytes), all checksums verified");
        if (result.Value.MasterKeyRestored)
        {
            var legacy = Path.Combine(Path.GetFullPath(dataPath), ServerOptions.LegacyMasterKeyFileName);
            Console.WriteLine($"  the archive holds a master key (an earlier version took it in), written to {legacy}:");
            Console.WriteLine($"  move it out of the data directory and set {ServerOptions.Section}:{nameof(ServerOptions.MasterKeyFile)} to it, "
                + $"or set {ServerOptions.Section}:{nameof(ServerOptions.MasterKey)} to its Base64 (base64 < {legacy})");
        }
        else
        {
            Console.WriteLine($"  the server needs the master key these access keys were encrypted with ({ServerOptions.Section}:{nameof(ServerOptions.MasterKey)})");
        }
        Console.WriteLine($"  archives hold no replication rules: {SetUpAgain}");
        return Success;
    }

    private static async Task<int> RestoreMetadataAsync(
        string archive, string dataPath, ServerOptions options, bool once, bool allowMissing)
    {
        // The server's own lock: held by a running server, so the metadata is never replaced under it.
        using var held = DataDirectoryLock.TryAcquire(dataPath);
        if (held is null)
        {
            var inUse = Application.Features.Backups.BackupErrors.RestoreInUse;
            await Console.Error.WriteLineAsync($"Restore failed ({inUse.Code}): {inUse.Message}");
            return Failure;
        }

        var restorer = new MetadataRestorer(
            new TarBackupArchiveFormat(), new SqliteMetadataFiles(),
            new FileSystemBlobStore(new FileSystemBlobOptions(dataPath, options.NodeId)));
        var result = await restorer.RestoreAsync(
            new MetadataRestoreRequest(archive, dataPath, SqliteMetadataInitializer.LatestVersion, once, allowMissing),
            DateTime.UtcNow, CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync($"Restore failed ({result.Error.Code}): {result.Error.Message}");
            return Failure;
        }

        if (result.Value.WasSkipped)
        {
            Console.WriteLine($"Restore skipped: {ArchiveSource.DisplayName(archive)} is already restored in {Path.GetFullPath(dataPath)} ({OnceOption}).");
            return Success;
        }

        var summary = result.Value;
        var manifest = summary.Manifest!;
        Console.WriteLine($"Restored the metadata of {ArchiveSource.DisplayName(archive)} into {Path.GetFullPath(dataPath)}:");
        Console.WriteLine($"  taken at {manifest.SnapshotAt:u} on {manifest.NodeId} (TimeStore {manifest.TimeStoreVersion})");
        Console.WriteLine($"  {manifest.Buckets} buckets, {manifest.Objects} objects, {manifest.Versions} versions; "
            + $"{summary.Referenced - summary.Missing} of {summary.Referenced} referenced blobs present"
            + (summary.Missing > 0 ? $", {summary.Missing} MISSING ({AllowMissingOption}): those objects fail to read" : ""));
        Console.WriteLine($"  the replaced metadata is in {summary.PreviousCopy} (delete it once the server is checked)");
        PrintDroppedRules(summary.DroppedRules);
        return Success;
    }

    /// <summary>Archives hold no replication rules: the ones the restore replaced are named, to set up again.</summary>
    private static void PrintDroppedRules(IReadOnlyList<Domain.Stores.ReplicationRuleSummary>? rules)
    {
        if (rules is null)
        {
            Console.WriteLine($"  the replaced replication rules could not be read: check each bucket's replication; archives hold none, {SetUpAgain}");
            return;
        }
        if (rules.Count == 0)
            return;

        Console.WriteLine($"  replication rules NOT restored (archives hold none), {SetUpAgain}:");
        foreach (var rule in rules)
        {
            var prefix = rule.Prefix.Length == 0 ? "whole bucket" : $"prefix {rule.Prefix}";
            var deletes = rule.ReplicateDeletes ? "deletes copied" : "deletes not copied";
            Console.WriteLine($"    {rule.Bucket} → {rule.Endpoint.TrimEnd('/')}/{rule.TargetBucket} ({prefix}, {deletes})");
        }
    }

    private static int PrintUsage(string problem)
    {
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine(
            $"Usage: TimeStore.Server {Name} {ArchiveOption} <file or http(s) URL> [{DataPathOption} <empty directory>] [{IfEmptyOption}]");
        Console.Error.WriteLine(
            $"       TimeStore.Server {Name} {MetadataOption} {ArchiveOption} <file or http(s) URL> [{DataPathOption} <data directory>] "
            + $"[{OnceOption}] [{AllowMissingOption}]");
        return Usage;
    }
}
