using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;

namespace TimeStore.Infra.Services.Objects;

/// <summary>
/// A multipart object read as one seekable stream over its part blobs. One part is open at a time: an object may have
/// 10 000 parts, far more than it is reasonable to hold file handles for. The part holding the start position is
/// opened before the stream is handed out (see <see cref="OpenAsync"/>), so a read that cannot start fails before any
/// response byte is sent; later parts are opened when the read reaches them.
/// </summary>
internal sealed class MultipartReadStream : Stream
{
    private readonly IBlobStore _blobs;
    private readonly MultipartBlobs _data;
    private readonly long _length;
    private Stream? _current;
    private int _currentIndex = -1;
    private long _position;

    private MultipartReadStream(IBlobStore blobs, MultipartBlobs data)
    {
        _blobs = blobs;
        _data = data;
        _length = data.Size;
    }

    /// <summary>Opens the stream with its first part already open (throws <c>FileNotFoundException</c> like a single blob).</summary>
    public static async ValueTask<MultipartReadStream> OpenAsync(IBlobStore blobs, MultipartBlobs data, CancellationToken ct)
    {
        var stream = new MultipartReadStream(blobs, data);
        try
        {
            if (data.Parts.Count > 0)
                await stream.OpenPartAsync(0, ct);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target; // the right part is opened (or re-positioned) by the next read
        return _position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= _length)
            return 0;

        var stream = await PositionAsync(cancellationToken);
        var part = _data.Parts[_currentIndex];
        var available = part.Offset + part.Blob.Size - _position;
        var read = await stream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, available)], cancellationToken);
        if (read == 0)
            throw new IOException($"Part {part.PartNumber} ended before its recorded size of {part.Blob.Size} bytes.");
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>The open stream of the part holding <see cref="_position"/>, at the right offset inside it.</summary>
    private async ValueTask<Stream> PositionAsync(CancellationToken ct)
    {
        var index = _data.IndexOfPartAt(_position);
        if (index != _currentIndex || _current is null)
            await OpenPartAsync(index, ct);

        var inPart = _position - _data.Parts[index].Offset;
        if (_current!.Position != inPart)
            _current.Seek(inPart, SeekOrigin.Begin);
        return _current;
    }

    private async ValueTask OpenPartAsync(int index, CancellationToken ct)
    {
        if (_current is not null)
            await _current.DisposeAsync();
        _current = null;
        _current = await _blobs.OpenReadAsync(_data.Parts[index].Blob, ct);
        _currentIndex = index;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _current?.Dispose();
        _current = null;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_current is not null)
            await _current.DisposeAsync();
        _current = null;
        await base.DisposeAsync();
    }
}
