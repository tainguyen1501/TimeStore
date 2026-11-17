using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Stores;

namespace TimeStore.Blob.FileSystem;

/// <param name="RootPath">Data directory; blobs go to <c>blobs/</c>, in-flight writes to <c>tmp/</c>.</param>
/// <param name="NodeId">This node, recorded in every <see cref="BlobLocator"/>.</param>
public sealed record FileSystemBlobOptions(string RootPath, string NodeId);

/// <summary>
/// Blobs as files named by id (never by object key: no path length, character or case-sensitivity problems).
/// Write: temp file → fsync → atomic rename. The body is streamed through a pooled buffer, hashed on the way.
/// </summary>
public sealed class FileSystemBlobStore(FileSystemBlobOptions options) : IBlobStore, IStorageInitializer
{
    public const string BlobsDirectory = "blobs";
    public const string TempDirectory = "tmp";
    internal const string TempSearchPattern = "*.tmp";
    private const string TempExtension = ".tmp";
    private const string IdFormat = "N";
    private const int CopyBufferSize = 128 * 1024;
    private const int ShardLength = 2;
    private const int BlobNameLength = 32;
    private const string ShardFormat = "x2"; // matches the last two hex characters of an id in "N" format

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        // All 256 shard directories up front: the write path never creates directories.
        var blobs = Directory.CreateDirectory(Path.Combine(options.RootPath, BlobsDirectory));
        for (var shard = 0; shard <= byte.MaxValue; shard++)
            blobs.CreateSubdirectory(shard.ToString(ShardFormat));
        var temp = Directory.CreateDirectory(Path.Combine(options.RootPath, TempDirectory));

        // Single node: nothing writes before startup completes, so leftovers are from a crash.
        foreach (var leftover in temp.EnumerateFiles(TempSearchPattern))
            leftover.Delete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask<BlobLocator> WriteAsync(Stream source, CancellationToken ct)
    {
        var blobId = Guid.CreateVersion7();
        var name = blobId.ToString(IdFormat);
        var tempPath = Path.Combine(options.RootPath, TempDirectory, name + TempExtension);
        var finalPath = BlobPath(name);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long size = 0;
        try
        {
            await using (var file = new FileStream(tempPath, WriteOptions))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }

                file.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath);
            DirectorySync.Flush(Path.GetDirectoryName(finalPath)!); // the new name is durable before the commit
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new BlobLocator(blobId, options.NodeId, size, Convert.ToHexStringLower(md5.GetHashAndReset()));
    }

    public ValueTask<Stream> OpenReadAsync(BlobLocator blob, CancellationToken ct) =>
        ValueTask.FromResult<Stream>(new FileStream(BlobPath(blob.BlobId.ToString(IdFormat)), ReadOptions));

    public ValueTask DeleteAsync(Guid blobId, CancellationToken ct)
    {
        File.Delete(BlobPath(blobId.ToString(IdFormat)));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<BlobFile> ListAsync(DateTime writtenBefore, [EnumeratorCancellation] CancellationToken ct)
    {
        var blobs = new DirectoryInfo(Path.Combine(options.RootPath, BlobsDirectory));
        for (var shard = 0; shard <= byte.MaxValue; shard++)
        {
            var shardName = shard.ToString(ShardFormat);
            var directory = new DirectoryInfo(Path.Combine(blobs.FullName, shardName));
            if (!directory.Exists)
                continue;

            foreach (var file in directory.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                // Only "<32 hex>" in the shard of its last two characters is a blob; skip everything else.
                if (file.Name.Length != BlobNameLength || !file.Name.EndsWith(shardName, StringComparison.Ordinal)
                    || !Guid.TryParseExact(file.Name, IdFormat, out var blobId)
                    || !string.Equals(blobId.ToString(IdFormat), file.Name, StringComparison.Ordinal))
                    continue;

                DateTime writtenAt;
                long size;
                try
                {
                    writtenAt = file.LastWriteTimeUtc;
                    size = file.Length;
                }
                catch (FileNotFoundException)
                {
                    continue; // deleted while listing
                }

                if (writtenAt < writtenBefore)
                    yield return new BlobFile(blobId, size, writtenAt);
            }

            await Task.Yield(); // one shard at a time: keep a long listing from holding a thread
        }
    }

    // UUIDv7 starts with the timestamp, so shard by the random tail to spread files across directories.
    private string BlobPath(string name) =>
        Path.Combine(options.RootPath, BlobsDirectory, name[^ShardLength..], name);

    private static readonly FileStreamOptions WriteOptions = new()
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0, // the pooled copy buffer is the only buffer
    };

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0,
    };
}
