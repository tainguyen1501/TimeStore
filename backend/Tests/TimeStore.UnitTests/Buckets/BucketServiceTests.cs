using TimeStore.Application.Features.Buckets;
using TimeStore.Contracts.Buckets;
using TimeStore.Infra.Services.Buckets;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Buckets;

public sealed class BucketServiceTests
{
    private const string Name = "photos";
    private readonly InMemoryMetadataStore _store = new();
    private readonly RecordingBlobStore _blobs = new();
    private readonly BucketService _service;

    public BucketServiceTests() => _service = new BucketService(_store, Deletions.Immediate(_blobs), new TimeStore.Infra.Services.Policies.BucketPolicyCache(_store), new FixedTimeProvider());
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_WithValidName_ReturnsBucketWithMillisecondTimestamp()
    {
        var result = await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(Name, result.Value.Name);
        Assert.Equal(new DateTime(2026, 11, 16, 9, 30, 15, 123, DateTimeKind.Utc), result.Value.CreatedAt);
    }

    [Fact]
    public async Task Create_WithInvalidName_ReturnsInvalidName()
    {
        var result = await _service.CreateAsync(new CreateBucketRequest("Bad_Name"), _ct);

        Assert.Equal(BucketErrors.Codes.InvalidName, result.Error.Code);
    }

    [Fact]
    public async Task Create_WithTakenName_ReturnsAlreadyExists()
    {
        await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        var result = await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        Assert.Equal(BucketErrors.Codes.AlreadyExists, result.Error.Code);
    }

    [Fact]
    public async Task List_ReturnsCreatedBucketsByName()
    {
        await _service.CreateAsync(new CreateBucketRequest("videos"), _ct);
        await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        var buckets = await _service.ListAsync(_ct);

        Assert.Equal(new[] { Name, "videos" }, buckets.Select(b => b.Name));
    }

    [Fact]
    public async Task Get_WithExistingBucket_ReturnsIt()
    {
        await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        var result = await _service.GetAsync(Name, _ct);

        Assert.Equal(Name, result.Value.Name);
    }

    [Fact]
    public async Task Get_WithMissingBucket_ReturnsNotFound() =>
        Assert.Equal(BucketErrors.Codes.NotFound, (await _service.GetAsync(Name, _ct)).Error.Code);

    [Fact]
    public async Task Delete_WithExistingBucket_Succeeds()
    {
        await _service.CreateAsync(new CreateBucketRequest(Name), _ct);

        var result = await _service.DeleteAsync(Name, _ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(await _service.ListAsync(_ct));
    }

    [Fact]
    public async Task Delete_WithMissingBucket_ReturnsNotFound()
    {
        var result = await _service.DeleteAsync(Name, _ct);

        Assert.Equal(BucketErrors.Codes.NotFound, result.Error.Code);
    }

    [Fact]
    public async Task Delete_AbortsUploadsInProgress_AndFreesTheirParts()
    {
        var bucket = (await _service.CreateAsync(new CreateBucketRequest(Name), _ct)).Value;
        var upload = new TimeStore.Domain.Uploads.MultipartUpload(
            Guid.CreateVersion7(), bucket.Id, "big.bin", "application/octet-stream", null, null, [], [], DateTime.UtcNow);
        await _store.TryCreateUploadAsync(upload, _ct);
        var blob = new TimeStore.Domain.Blobs.BlobLocator(Guid.CreateVersion7(), "node-1", 10, new string('0', 32));
        await _store.CommitPartAsync(new TimeStore.Domain.Uploads.UploadPart(upload.UploadId, 1, blob, null, DateTime.UtcNow), _ct);

        Assert.True((await _service.DeleteAsync(Name, _ct)).IsSuccess);
        Assert.Equal([blob.BlobId], _blobs.Deleted);
    }
}
