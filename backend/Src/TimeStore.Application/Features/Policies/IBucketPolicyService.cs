using TimeStore.Primitives;

namespace TimeStore.Application.Features.Policies;

/// <summary>A bucket's policy document (S3 API and admin API).</summary>
public interface IBucketPolicyService
{
    /// <summary>The document as stored, or <c>POLICY_NOT_FOUND</c>.</summary>
    ValueTask<Result<string>> GetAsync(string bucket, CancellationToken ct);

    /// <summary>Validates (<see cref="BucketPolicy.Parse"/>) and stores the document; applies from the next request.</summary>
    ValueTask<Result> SetAsync(string bucket, string document, CancellationToken ct);

    /// <summary>Removes the policy; succeeds when there is none.</summary>
    ValueTask<Result> DeleteAsync(string bucket, CancellationToken ct);

    /// <summary>Whether the policy grants everyone (anonymous callers included) access without conditions.</summary>
    ValueTask<Result<bool>> IsPublicAsync(string bucket, CancellationToken ct);

    /// <summary>The bucket and folders made public with <see cref="SetPublicAsync"/> ("" = the whole bucket).</summary>
    ValueTask<Result<IReadOnlyList<string>>> GetPublicPrefixesAsync(string bucket, CancellationToken ct);

    /// <summary>
    /// Makes a bucket ("") or a folder public (anyone may read its objects) or private again by editing TimeStore's own
    /// statement in the policy (<see cref="PublicAccess"/>); other statements stay. Returns the public prefixes.
    /// </summary>
    ValueTask<Result<IReadOnlyList<string>>> SetPublicAsync(string bucket, string prefix, bool isPublic, CancellationToken ct);
}

/// <summary>
/// Who is calling: an access key (with its S3 scope), the root key, or nobody (anonymous, <see cref="AccessKeyId"/> null).
/// </summary>
public sealed record S3Caller(string? AccessKeyId, bool IsRoot, string S3Scope)
{
    public bool IsAnonymous => AccessKeyId is null;
}

/// <summary>
/// Decides whether a caller may perform an S3 action (docs/research/s3-bucket-policy.md): the root key always; else an
/// explicit Deny refuses, an Allow grants, and without either only a full-scope key is allowed.
/// </summary>
public interface IS3Authorizer
{
    /// <param name="bucket">The bucket the action is on, or null (ListBuckets, CreateBucket: no policy applies).</param>
    /// <param name="action">IAM action name, e.g. <c>s3:GetObject</c>.</param>
    /// <param name="resource">The ARN of the bucket or object.</param>
    /// <param name="context">Builds the condition context; only called when a policy is evaluated (the common case,
    /// a bucket without a policy, builds nothing).</param>
    ValueTask<bool> IsAllowedAsync(
        string? bucket, string action, string resource, S3Caller caller, Func<IReadOnlyDictionary<string, string>> context,
        CancellationToken ct);
}
