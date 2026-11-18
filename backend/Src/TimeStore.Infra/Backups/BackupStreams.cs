using System.Security.Cryptography;

namespace TimeStore.Infra.Backups;

/// <summary>
/// Reads exactly <c>length</c> bytes of a source (fewer when it ends early) and hashes them with MD5 and SHA-256. It
/// reports the length and position (seekable, but never seeks) so <c>TarWriter</c> can write the header first.
/// </summary>
internal sealed class HashingReadStream(Stream source, long length) : Stream
{
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public Application.Abstractions.ArchiveEntryHashes Hashes() => new(
        Convert.ToHexStringLower(_md5.GetHashAndReset()), Convert.ToHexStringLower(_sha256.GetHashAndReset()));

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer[..Limit(buffer.Length)]);
        Hash(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken);
        Hash(buffer.Span[..read]);
        return read;
    }

    private int Limit(int requested) => (int)Math.Min(requested, length - _position);

    private void Hash(ReadOnlySpan<byte> data)
    {
        _md5.AppendData(data);
        _sha256.AppendData(data);
        _position += data.Length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _md5.Dispose();
            _sha256.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Passes writes through to a destination, counting and hashing (SHA-256) them. Leaves the destination open.</summary>
internal sealed class HashingWriteStream(Stream destination) : Stream
{
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private volatile bool _detached;

    public long Written { get; private set; }

    /// <summary>
    /// Drops every later write: an abandoned archive must not get its end blocks and closing frame, or a truncated
    /// copy would look complete to tools that do not check <c>blobs.sha256</c>.
    /// </summary>
    public void Detach() => _detached = true;

    public string Sha256Hex() => Convert.ToHexStringLower(_sha256.GetHashAndReset());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_detached)
            return;
        destination.Write(buffer);
        _sha256.AppendData(buffer);
        Written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_detached)
            return;
        await destination.WriteAsync(buffer, cancellationToken);
        _sha256.AppendData(buffer.Span);
        Written += buffer.Length;
    }

    public override void Flush()
    {
        if (!_detached)
            destination.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _detached ? Task.CompletedTask : destination.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _sha256.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Replays bytes already read from a source (format detection), then reads the rest of it.</summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream source) : Stream
{
    private ReadOnlyMemory<byte> _prefix = prefix;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefix.IsEmpty)
            return source.Read(buffer);
        var count = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..count].CopyTo(buffer);
        _prefix = _prefix[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefix.IsEmpty)
            return source.ReadAsync(buffer, cancellationToken);
        var count = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..count].CopyTo(buffer.Span);
        _prefix = _prefix[count..];
        return ValueTask.FromResult(count);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            source.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => source.DisposeAsync();
}
