namespace TimeStore.Contracts.Buckets;

public sealed record CreateBucketRequest(string Name);

/// <param name="Versioning"><c>Unversioned</c> (never enabled), <c>Enabled</c> or <c>Suspended</c>.</param>
public sealed record BucketResponse(Guid Id, string Name, DateTime CreatedAt, string Versioning);

/// <param name="Status"><c>Enabled</c> or <c>Suspended</c> (a bucket never returns to unversioned).</param>
public sealed record SetBucketVersioningRequest(string Status);
