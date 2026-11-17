using TimeStore.Contracts.Buckets;
using TimeStore.Domain.Buckets;

namespace TimeStore.Application.Features.Buckets;

public static class BucketMappings
{
    extension(Bucket bucket)
    {
        public BucketResponse ToResponse() => new(bucket.Id, bucket.Name, bucket.CreatedAt, bucket.Versioning.ToString());
    }
}
