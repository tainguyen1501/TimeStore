using TimeStore.Contracts.Buckets;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Buckets;

/// <summary>Bucket use cases, shared by the S3 API and the admin API.</summary>
public interface IBucketService
{
    ValueTask<Result<BucketResponse>> CreateAsync(CreateBucketRequest request, CancellationToken ct);

    ValueTask<IReadOnlyList<BucketResponse>> ListAsync(CancellationToken ct);

    /// <summary>The bucket, or <c>BUCKET_NOT_FOUND</c>.</summary>
    ValueTask<Result<BucketResponse>> GetAsync(string name, CancellationToken ct);

    ValueTask<Result> DeleteAsync(string name, CancellationToken ct);

    /// <summary>The bucket's versioning state (<see cref="Domain.Objects.BucketVersioning"/>), or <c>BUCKET_NOT_FOUND</c>.</summary>
    ValueTask<Result<Domain.Objects.BucketVersioning>> GetVersioningAsync(string name, CancellationToken ct);

    /// <summary>Admin API: <c>Enabled</c> or <c>Suspended</c> from a request; <c>BUCKET_INVALID_VERSIONING</c> otherwise.</summary>
    ValueTask<Result<BucketResponse>> SetVersioningAsync(string name, SetBucketVersioningRequest request, CancellationToken ct);

    /// <summary>Enabled or Suspended (never back to unversioned), or <c>BUCKET_NOT_FOUND</c>.</summary>
    ValueTask<Result> SetVersioningAsync(string name, Domain.Objects.BucketVersioning versioning, CancellationToken ct);
}
