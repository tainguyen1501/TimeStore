using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Backups;
using TimeStore.Domain.Backups;

namespace TimeStore.Infra.Backups;

/// <summary>
/// <see cref="IBackupArchiveFormat"/> on the base library only (Native AOT): <see cref="TarWriter"/> (PAX) inside a
/// <see cref="ZstandardStream"/> with its content checksum, entries hashed (MD5 and SHA-256) while they are copied,
/// the output counted and hashed. Nothing seeks, so the archive streams into an HTTP response.
/// </summary>
public sealed class TarBackupArchiveFormat : IBackupArchiveFormat
{
    public const string ZstdExtension = ".tar.zst";
    public const string TarExtension = ".tar";

    // Zstandard frame magic number (RFC 8878), little-endian 0xFD2FB528.
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    // Entries are private to the server's user, like the data directory (blobs, the database, the master key).
    private const UnixFileMode EntryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string ExtensionOf(string compression) => compression switch
    {
        BackupCompressions.Zstd => ZstdExtension,
        BackupCompressions.None => TarExtension,
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    public IBackupArchiveWriter CreateWriter(Stream destination, string compression, int level) =>
        new Writer(destination, compression, level);

    public async ValueTask<IBackupArchiveReader> OpenReaderAsync(Stream source, CancellationToken ct)
    {
        var head = new byte[ZstdMagic.Length];
        var read = await source.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var replayed = new PrefixedStream(head.AsMemory(0, read), source);
        return read == head.Length && head.AsSpan().SequenceEqual(ZstdMagic)
            ? new Reader(new ZstandardStream(replayed, CompressionMode.Decompress), BackupCompressions.Zstd)
            : new Reader(replayed, BackupCompressions.None);
    }

    public byte[] SerializeManifest(BackupManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest);

    public BackupManifest? ParseManifest(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, BackupJsonContext.Default.BackupManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Writer : IBackupArchiveWriter
    {
        private readonly HashingWriteStream _output;
        private readonly ZstandardStream? _zstd;
        private readonly TarWriter _tar;
        private bool _completed;

        public Writer(Stream destination, string compression, int level)
        {
            _output = new HashingWriteStream(destination);
            _zstd = compression switch
            {
                BackupCompressions.Zstd => new ZstandardStream(
                    _output, new ZstandardCompressionOptions { Quality = level, AppendChecksum = true }, leaveOpen: true),
                BackupCompressions.None => null,
                _ => throw new ArgumentOutOfRangeException(nameof(compression)),
            };
            _tar = new TarWriter((Stream?)_zstd ?? _output, TarEntryFormat.Pax, leaveOpen: true);
        }

        public async ValueTask<ArchiveEntryHashes> AddAsync(string name, Stream content, long length, CancellationToken ct)
        {
            // TarWriter needs the length up front when the archive stream cannot seek: the wrapper reports it, reads
            // exactly that much and hashes on the way (one read of the source).
            using var data = new HashingReadStream(content, length);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = EntryMode };
            await _tar.WriteEntryAsync(entry, ct);
            if (data.Position != length)
                throw new InvalidDataException($"Entry '{name}' ended after {data.Position} of {length} bytes.");
            return data.Hashes();
        }

        public async ValueTask<ArchiveSummary> CompleteAsync(CancellationToken ct)
        {
            _completed = true;
            await _tar.DisposeAsync(); // end-of-archive blocks
            if (_zstd is not null)
                await _zstd.DisposeAsync(); // last block and content checksum
            await _output.FlushAsync(ct);
            return new ArchiveSummary(_output.Written, _output.Sha256Hex());
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                // An abandoned archive: release the native compressor without ending the archive.
                _output.Detach();
                try
                {
                    await _tar.DisposeAsync();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
                {
                }

                if (_zstd is not null)
                {
                    try
                    {
                        await _zstd.DisposeAsync();
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
                    {
                    }
                }
            }

            _output.Dispose();
        }
    }

    private sealed class Reader(Stream archive, string compression) : IBackupArchiveReader
    {
        private readonly TarReader _tar = new(archive, leaveOpen: true);
        private bool _ended;

        public string Compression => compression;

        public async ValueTask<ArchiveEntry?> NextAsync(CancellationToken ct)
        {
            if (_ended)
                return null;

            while (await _tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                switch (entry.EntryType)
                {
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                        return new Entry(entry);
                    case TarEntryType.Directory:
                        continue; // written by tools that repacked an archive; names are checked per file
                    default:
                        throw new InvalidDataException($"Entry '{entry.Name}' is a {entry.EntryType}, not a file.");
                }
            }

            // Read past the tar end blocks to the end of the Zstandard frame: only then is its checksum verified.
            _ended = true;
            await archive.CopyToAsync(Stream.Null, ct);
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _tar.DisposeAsync();
            await archive.DisposeAsync();
        }

        private sealed class Entry(TarEntry entry) : ArchiveEntry(entry.Name, entry.Length)
        {
            public override async ValueTask<ArchiveEntryHashes> CopyToAsync(Stream destination, CancellationToken ct)
            {
                using var data = new HashingReadStream(entry.DataStream ?? Stream.Null, entry.Length);
                await data.CopyToAsync(destination, ct);
                if (data.Position != entry.Length)
                    throw new InvalidDataException($"Entry '{entry.Name}' is truncated.");
                return data.Hashes();
            }
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
