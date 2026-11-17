namespace TimeStore.Domain.Buckets;

/// <summary>A bucket. Immutable record: changes produce a new value that a store commits.</summary>
public sealed record Bucket(Guid Id, string Name, DateTime CreatedAt)
{
    /// <summary>Versioning state; stores read it with the bucket, writes decide from it inside their transaction.</summary>
    public Objects.BucketVersioning Versioning { get; init; }

    public const int NameMinLength = 3;
    public const int NameMaxLength = 63;
}
