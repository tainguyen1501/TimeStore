namespace TimeStore.Contracts.Policies;

/// <summary>
/// A bucket's policy for the admin API: the JSON document as stored, and whether it makes the bucket public
/// (grants everyone, anonymous callers included, access without conditions).
/// </summary>
public sealed record BucketPolicyResponse(string Document, bool IsPublic);

/// <summary>Replaces the bucket's policy (IAM JSON, at most 20 KB; docs/research/s3-bucket-policy.md).</summary>
public sealed record SetBucketPolicyRequest(string Document);

/// <summary>The bucket ("") and folders ("photos/") made public from the admin UI: anyone may read their objects.</summary>
public sealed record PublicAccessResponse(IReadOnlyList<string> Prefixes);

/// <param name="Prefix">"" for the whole bucket, or a folder ending in '/'.</param>
public sealed record SetPublicAccessRequest(string Prefix, bool IsPublic);
