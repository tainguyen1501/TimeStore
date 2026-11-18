using TimeStore.Application.Features.Backups;

namespace TimeStore.Application.Abstractions;

/// <summary>
/// The backup archive format: tar (PAX) with optional Zstandard around it, entries hashed on the way, the manifest as
/// JSON. The one entry point to the archive, compression, hashing and JSON libraries for backups.
/// </summary>
public interface IBackupArchiveFormat
{
    /// <summary>File extension of an archive with this compression (<c>.tar.zst</c>, <c>.tar</c>).</summary>
    string ExtensionOf(string compression);

    /// <param name="level">Zstandard level (ignored without compression).</param>
    IBackupArchiveWriter CreateWriter(Stream destination, string compression, int level);

    /// <summary>Opens an archive written by <see cref="CreateWriter"/>; the compression is detected from its first bytes.</summary>
    ValueTask<IBackupArchiveReader> OpenReaderAsync(Stream source, CancellationToken ct);

    byte[] SerializeManifest(BackupManifest manifest);

    /// <summary>Null when the bytes are not a manifest.</summary>
    BackupManifest? ParseManifest(ReadOnlySpan<byte> json);
}

/// <summary>Writes entries in order; nothing seeks, so the destination may be a network stream.</summary>
public interface IBackupArchiveWriter : IAsyncDisposable
{
    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes of <paramref name="content"/> into entry <paramref name="name"/>
    /// and returns their hashes. Fails (<see cref="InvalidDataException"/>) when the content is shorter.
    /// </summary>
    ValueTask<ArchiveEntryHashes> AddAsync(string name, Stream content, long length, CancellationToken ct);

    /// <summary>Ends the archive (tar end blocks, Zstandard frame) and flushes the destination.</summary>
    ValueTask<ArchiveSummary> CompleteAsync(CancellationToken ct);
}

public interface IBackupArchiveReader : IAsyncDisposable
{
    /// <summary>The compression the archive was written with (<c>zstd</c> or <c>none</c>).</summary>
    string Compression { get; }

    /// <summary>The next file entry, or null at the end. Its content is readable until the next call.</summary>
    ValueTask<ArchiveEntry?> NextAsync(CancellationToken ct);
}

/// <summary>A file entry of an archive being read; valid until the reader moves to the next one.</summary>
public abstract class ArchiveEntry(string name, long length)
{
    public string Name => name;

    public long Length => length;

    /// <summary>Copies the entry's content to <paramref name="destination"/> and returns its hashes.</summary>
    public abstract ValueTask<ArchiveEntryHashes> CopyToAsync(Stream destination, CancellationToken ct);
}

/// <param name="Md5Hex">Lowercase hex MD5 of the entry's bytes (what blob locators record).</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 of the entry's bytes (what <c>blobs.sha256</c> records).</param>
public readonly record struct ArchiveEntryHashes(string Md5Hex, string Sha256Hex);

/// <param name="Size">Bytes written to the destination (after compression).</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 of those bytes, to check a download.</param>
public readonly record struct ArchiveSummary(long Size, string Sha256Hex);
