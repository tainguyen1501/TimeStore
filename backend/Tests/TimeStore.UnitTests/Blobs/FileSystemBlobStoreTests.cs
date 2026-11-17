using System.Security.Cryptography;
using TimeStore.Blob.FileSystem;

namespace TimeStore.UnitTests.Blobs;

public sealed class FileSystemBlobStoreTests : IDisposable
{
    private const string NodeId = "node-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "timestore-blobs", Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _store;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public FileSystemBlobStoreTests() => _store = new FileSystemBlobStore(new FileSystemBlobOptions(_root, NodeId));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Write_ReturnsLocatorWithSizeAndMd5_AndReadsBack()
    {
        await _store.InitializeAsync(_ct);
        var content = RandomNumberGenerator.GetBytes(300_000); // larger than the copy buffer

        var blob = await _store.WriteAsync(new MemoryStream(content), _ct);

        Assert.Equal(NodeId, blob.NodeId);
        Assert.Equal(content.Length, blob.Size);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(content)), blob.Md5Hex);
        await using var read = await _store.OpenReadAsync(blob, _ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, _ct);
        Assert.Equal(content, copy.ToArray());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, FileSystemBlobStore.TempDirectory)));
    }

    [Fact]
    public async Task Delete_RemovesBlob_AndIsIdempotent()
    {
        await _store.InitializeAsync(_ct);
        var blob = await _store.WriteAsync(new MemoryStream([1, 2, 3]), _ct);

        await _store.DeleteAsync(blob.BlobId, _ct);
        await _store.DeleteAsync(blob.BlobId, _ct);

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _store.OpenReadAsync(blob, _ct));
    }

    [Fact]
    public async Task Initialize_RemovesLeftoverTempFiles()
    {
        var temp = Directory.CreateDirectory(Path.Combine(_root, FileSystemBlobStore.TempDirectory));
        await File.WriteAllTextAsync(Path.Combine(temp.FullName, "crashed.tmp"), "partial", _ct);

        await _store.InitializeAsync(_ct);

        Assert.Empty(temp.EnumerateFiles());
    }
}
