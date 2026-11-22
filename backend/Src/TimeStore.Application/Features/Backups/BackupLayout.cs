using System.Globalization;
using TimeStore.Domain.Backups;

namespace TimeStore.Application.Features.Backups;

/// <summary>
/// What a backup archive holds and how its entries are named (docs/research/backup.md section 2). The layout mirrors
/// the data directory, so <c>tar -xf</c> into an empty directory gives a data directory, and <c>sha256sum -c
/// blobs.sha256</c> checks it.
/// </summary>
public static class BackupLayout
{
    /// <summary>Archive format version; a restore refuses newer ones.</summary>
    public const int FormatVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string MetadataEntry = "metadata.db";
    public const string ChecksumsEntry = "blobs.sha256";
    public const string MasterKeyEntry = "master.key";
    public const string BlobsDirectory = "blobs";

    private const string ArchivePrefix = "timestore-";
    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string IdFormat = "N";
    private const int ShardLength = 2;
    private const int IdLength = 32;
    private const int Sha256HexLength = 64;
    private const string ChecksumSeparator = "  "; // sha256sum: two spaces between hash and file name
    private const char EntrySeparator = '/';
    private const string NameSeparator = "-";
    private const int FirstSequence = 1;
    private const string LineEnd = "\n";

    /// <summary>
    /// <c>timestore-&lt;node&gt;-&lt;yyyyMMddTHHmmssZ&gt;&lt;extension&gt;</c>; a later archive of the same second (a
    /// metadata-only backup takes milliseconds) gets <c>-2</c>, <c>-3</c>, ... before the extension.
    /// </summary>
    public static string ArchiveName(string nodeId, DateTime startedAt, string extension, int sequence = FirstSequence) =>
        ArchivePrefix + nodeId + NameSeparator + startedAt.ToString(TimeFormat, CultureInfo.InvariantCulture)
        + (sequence > FirstSequence ? NameSeparator + sequence.ToString(CultureInfo.InvariantCulture) : string.Empty) + extension;

    /// <summary><c>blobs/&lt;last two hex characters&gt;/&lt;id&gt;</c>, the data directory's layout.</summary>
    public static string BlobEntry(Guid blobId)
    {
        var name = blobId.ToString(IdFormat);
        return BlobsDirectory + EntrySeparator + name[^ShardLength..] + EntrySeparator + name;
    }

    /// <summary>Accepts exactly the names <see cref="BlobEntry"/> writes (no other path can reach the disk).</summary>
    public static bool TryParseBlobEntry(string entry, out Guid blobId)
    {
        blobId = Guid.Empty;
        var prefixLength = BlobsDirectory.Length + 1 + ShardLength + 1;
        if (entry.Length != prefixLength + IdLength || !entry.StartsWith(BlobsDirectory + EntrySeparator, StringComparison.Ordinal)
            || entry[BlobsDirectory.Length + 1 + ShardLength] != EntrySeparator)
            return false;
        var name = entry[prefixLength..];
        return Guid.TryParseExact(name, IdFormat, out blobId)
            && string.Equals(blobId.ToString(IdFormat), name, StringComparison.Ordinal)
            && string.Equals(entry, BlobEntry(blobId), StringComparison.Ordinal);
    }

    /// <summary>One line of <c>blobs.sha256</c> (sha256sum format).</summary>
    public static string ChecksumLine(string sha256Hex, string entry) => sha256Hex + ChecksumSeparator + entry + LineEnd;

    public static bool TryParseChecksumLine(string line, out string sha256Hex, out string entry)
    {
        sha256Hex = entry = string.Empty;
        if (line.Length <= Sha256HexLength + ChecksumSeparator.Length
            || string.CompareOrdinal(line, Sha256HexLength, ChecksumSeparator, 0, ChecksumSeparator.Length) != 0)
            return false;
        sha256Hex = line[..Sha256HexLength];
        entry = line[(Sha256HexLength + ChecksumSeparator.Length)..];
        foreach (var c in sha256Hex)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}

/// <summary>The first entry of every archive: what it holds, so a restore can check it before unpacking.</summary>
/// <param name="Format"><see cref="BackupLayout.FormatVersion"/> of the writer.</param>
/// <param name="SnapshotAt">Instant T.</param>
/// <param name="SchemaVersion">Metadata schema version of the snapshot; a restore refuses one newer than it knows.</param>
/// <param name="Blobs">Blob entries in the archive.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="MetadataSha256">Lowercase hex SHA-256 of <c>metadata.db</c>.</param>
/// <param name="Content"><c>full</c>, or <c>metadata</c> (no blob entries: <paramref name="Blobs"/> is 0).</param>
/// <param name="ReferencedBlobs">Blobs the metadata references (and their bytes): in the archive when full, on the
/// data directory it came from when metadata only.</param>
/// <param name="RetainDeletedDays">The blob retention when the backup was taken (metadata only: how long it stays
/// restorable).</param>
public sealed record BackupManifest(
    int Format,
    string TimeStoreVersion,
    string NodeId,
    DateTime SnapshotAt,
    int SchemaVersion,
    string Compression,
    long Buckets,
    long Objects,
    long Versions,
    long Blobs,
    long Bytes,
    long MetadataBytes,
    string MetadataSha256,
    bool IncludesMasterKey,
    string Content = BackupContents.Full,
    long ReferencedBlobs = 0,
    long ReferencedBytes = 0,
    int RetainDeletedDays = 0);
