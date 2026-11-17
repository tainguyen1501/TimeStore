using System.Text.Json;
using System.Text.Json.Serialization;
using TimeStore.Contracts.AccessKeys;
using TimeStore.Contracts.Auth;
using TimeStore.Contracts.Buckets;
using TimeStore.Contracts.Common;
using TimeStore.Contracts.Lifecycle;
using TimeStore.Contracts.Maintenance;
using TimeStore.Contracts.Metrics;
using TimeStore.Contracts.Objects;
using TimeStore.Contracts.Policies;

namespace TimeStore.Server.Admin;

/// <summary>Source-generated JSON (AOT). Every admin API request/response type must be listed here.</summary>
// Strict numbers: the Web defaults also read numbers from strings, which the OpenAPI schema then describes as
// "integer or string" (and the generated TypeScript as `number | string`).
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(CreateBucketRequest))]
[JsonSerializable(typeof(BucketResponse))]
[JsonSerializable(typeof(IReadOnlyList<BucketResponse>))]
[JsonSerializable(typeof(CreateAccessKeyRequest))]
[JsonSerializable(typeof(AccessKeyResponse))]
[JsonSerializable(typeof(IReadOnlyList<AccessKeyResponse>))]
[JsonSerializable(typeof(CreatedAccessKeyResponse))]
[JsonSerializable(typeof(SetAccessKeyRoleRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(BlobSweepResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ObjectListResponse))]
[JsonSerializable(typeof(ObjectDetailsResponse))]
[JsonSerializable(typeof(ObjectWrittenResponse))]
[JsonSerializable(typeof(DeleteObjectsRequest))]
[JsonSerializable(typeof(DeleteObjectsResponse))]
[JsonSerializable(typeof(StartUploadRequest))]
[JsonSerializable(typeof(StartUploadResponse))]
[JsonSerializable(typeof(UploadedPartResponse))]
[JsonSerializable(typeof(CompleteUploadRequest))]
[JsonSerializable(typeof(MetricsResponse))]
[JsonSerializable(typeof(SetBucketVersioningRequest))]
[JsonSerializable(typeof(ObjectVersionListResponse))]
[JsonSerializable(typeof(RestoreVersionRequest))]
[JsonSerializable(typeof(LifecycleConfigurationDto))]
[JsonSerializable(typeof(LifecycleRunResponse))]
[JsonSerializable(typeof(BucketPolicyResponse))]
[JsonSerializable(typeof(ObjectAccessResponse))]
[JsonSerializable(typeof(PublicAccessResponse))]
[JsonSerializable(typeof(SetPublicAccessRequest))]
[JsonSerializable(typeof(ShareObjectRequest))]
[JsonSerializable(typeof(ShareObjectResponse))]
[JsonSerializable(typeof(SetBucketPolicyRequest))]
[JsonSerializable(typeof(SetAccessKeyScopeRequest))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
