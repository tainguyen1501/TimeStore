using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Buckets;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Buckets;

/// <summary>Hand-written validation (no reflection-based validation library: AOT). Null means valid.</summary>
public static class BucketValidation
{
    public static AppError? Validate(CreateBucketRequest request) =>
        BucketNames.IsValid(request.Name) ? null : BucketErrors.InvalidName(request.Name);
}
