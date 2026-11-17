using TimeStore.Blob.FileSystem;
using TimeStore.Domain.Objects;
using TimeStore.Infra.Services.Objects;

namespace TimeStore.UnitTests.Objects;

public sealed class MultipartReadStreamTests : IAsyncLifetime
{
    private static readonly int[] PartSizes = [1000, 1, 4096, 777];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-mpread", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileSystemBlobStore _blobs = null!;
    private MultipartBlobs _data = null!;
    private byte[] _expected = null!;

    public async ValueTask InitializeAsync()
    {
        _blobs = new FileSystemBlobStore(new FileSystemBlobOptions(_root, "node-1"));
        await _blobs.InitializeAsync(_ct);
        var random = new Random(42);
        var parts = new List<ObjectPart>();
        var all = new List<byte>();
        var offset = 0L;
        for (var i = 0; i < PartSizes.Length; i++)
        {
            var bytes = new byte[PartSizes[i]];
            random.NextBytes(bytes);
            var blob = await _blobs.WriteAsync(new MemoryStream(bytes), _ct);
            parts.Add(new ObjectPart((i + 1) * 2, offset, blob, null)); // numbers with gaps: 2, 4, 6, 8
            offset += bytes.Length;
            all.AddRange(bytes);
        }

        _data = new MultipartBlobs(Guid.CreateVersion7(), parts);
        _expected = [.. all];
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Reads_TheConcatenationOfEveryPart()
    {
        await using var stream = await MultipartReadStream.OpenAsync(_blobs, _data, _ct);
        var copy = new MemoryStream();
        await stream.CopyToAsync(copy, bufferSize: 333, _ct);

        Assert.Equal(_expected.Length, stream.Length);
        Assert.Equal(_expected, copy.ToArray());
    }

    [Fact]
    public async Task SeekAndRead_AnywhereAcrossPartBoundaries()
    {
        await using var stream = await MultipartReadStream.OpenAsync(_blobs, _data, _ct);
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var start = random.Next(_expected.Length);
            var length = random.Next(1, _expected.Length - start + 1);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, _ct);
            Assert.Equal(_expected.AsSpan(start, length).ToArray(), buffer);
        }
    }

    [Fact]
    public async Task ReadPastTheEnd_ReturnsZero()
    {
        await using var stream = await MultipartReadStream.OpenAsync(_blobs, _data, _ct);
        stream.Position = _expected.Length + 10;

        Assert.Equal(0, await stream.ReadAsync(new byte[10], _ct));
    }

    [Fact]
    public async Task AMissingFirstPart_FailsBeforeAnyByte()
    {
        await _blobs.DeleteAsync(_data.Parts[0].Blob.BlobId, _ct);

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await MultipartReadStream.OpenAsync(_blobs, _data, _ct));
    }

    [Fact]
    public async Task ALaterPartDeletedMidRead_EndsTheReadWithAnIoError()
    {
        await using var stream = await MultipartReadStream.OpenAsync(_blobs, _data, _ct);
        await _blobs.DeleteAsync(_data.Parts[2].Blob.BlobId, _ct);

        await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(Stream.Null, _ct));
    }
}
