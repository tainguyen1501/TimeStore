using System.Text;
using TimeStore.Application.Features.Buckets;
using TimeStore.Contracts.Buckets;
using TimeStore.Contracts.Replication;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Replication;
using TimeStore.Primitives;

namespace TimeStore.Application.Features.Replication;

/// <summary>A valid rule request, normalized (endpoint without a trailing slash, default region, "" prefix).</summary>
public sealed record ValidReplicationRequest(
    Uri Endpoint, string Bucket, string Region, string AccessKeyId, string? SecretKey, string Prefix);

public static class ReplicationValidation
{
    private const string Http = "http";
    private const string Https = "https";
    private const string Root = "/";

    public static Result<ValidReplicationRequest> Validate(SetReplicationRequest request)
    {
        if (string.IsNullOrEmpty(request.Endpoint) || request.Endpoint.Length > ReplicationRule.EndpointMaxLength
            || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not (Http or Https) || string.IsNullOrEmpty(endpoint.Host)
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || endpoint.AbsolutePath != Root)
            return ReplicationErrors.InvalidEndpoint;

        if (BucketValidation.Validate(new CreateBucketRequest(request.Bucket ?? string.Empty)) is { } invalidBucket)
            return ReplicationErrors.InvalidDestination($"Destination bucket: {invalidBucket.Message}");

        var region = string.IsNullOrEmpty(request.Region) ? ReplicationRule.DefaultRegion : request.Region;
        if (region.Length > ReplicationRule.RegionMaxLength || !region.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            return ReplicationErrors.InvalidDestination("The region may hold lowercase letters, digits and hyphens only (up to 64).");

        if (!IsToken(request.AccessKeyId, ReplicationRule.AccessKeyIdMaxLength))
            return ReplicationErrors.InvalidDestination("The access key id is required: up to 256 characters, no spaces.");
        var secret = string.IsNullOrEmpty(request.SecretKey) ? null : request.SecretKey;
        if (secret is not null && !IsToken(secret, ReplicationRule.SecretKeyMaxLength))
            return ReplicationErrors.InvalidDestination("The secret key may hold up to 512 characters, no spaces.");

        var prefix = request.Prefix ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(prefix) > StoredObject.KeyMaxBytes || !ObjectKeys.IsListable(prefix))
            return ReplicationErrors.InvalidDestination("The prefix must be a valid object key prefix (up to 1024 bytes).");

        var normalized = new Uri($"{endpoint.Scheme}://{endpoint.Authority}", UriKind.Absolute);
        return new ValidReplicationRequest(normalized, request.Bucket!, region, request.AccessKeyId, secret, prefix);
    }

    private static bool IsToken(string? value, int maxLength) =>
        !string.IsNullOrEmpty(value) && value.Length <= maxLength && !value.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c));
}
