using TimeStore.Infra.Services.Policies;
using TimeStore.Application.Features.Policies;
using TimeStore.Infra.Services.Lifecycle;
using TimeStore.Application.Features.Lifecycle;
using TimeStore.Application.Features.Metrics;
using TimeStore.Infra.Services.Metrics;
using TimeStore.Infra.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.AccessKeys;
using TimeStore.Application.Features.Auth;
using TimeStore.Application.Features.Buckets;
using TimeStore.Infra.Services.Auth;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Infra.Services.Maintenance;
using TimeStore.Infra.Credentials;
using TimeStore.Infra.Services.AccessKeys;
using TimeStore.Infra.Caching;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Infra.Services.Uploads;
using TimeStore.Infra.Services.Buckets;
using TimeStore.Infra.Services.Objects;

namespace TimeStore.Infra;

public static class InfraServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services by hand (no assembly scanning: AOT). Services are stateless singletons; providers
    /// are registered by the host, never here. The host also registers <see cref="RootCredential"/>,
    /// <c>ISecretProtector</c>, <c>BlobSweepOptions</c>, <c>UploadExpiryOptions</c>, <c>AdminSessionOptions</c> and
    /// <c>BlobDeletionOptions</c> (they come from
    /// configuration).
    /// </summary>
    public static IServiceCollection AddTimeStoreServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.TryAddSingleton<ICacheService, MemoryCacheService>();
        services.AddSingleton<DeferredBlobDeletions>();
        services.AddSingleton<IBlobDeletions>(provider => provider.GetRequiredService<DeferredBlobDeletions>());
        services.AddSingleton<IBucketService, BucketService>();
        services.AddSingleton<IObjectService, ObjectService>();
        services.AddSingleton<IObjectCopyService, ObjectCopyService>();
        services.AddSingleton<IObjectBrowserService, ObjectBrowserService>();
        services.AddSingleton<IStorageMetricsService, StorageMetricsService>();
        services.AddSingleton<ILifecycleService, LifecycleService>();
        services.AddSingleton<ILifecycleRunner, LifecycleRunner>();
        services.AddSingleton<BucketPolicyCache>();
        services.AddSingleton<IBucketPolicyService, BucketPolicyService>();
        services.AddSingleton<IS3Authorizer, S3Authorizer>();
        services.AddSingleton<IUploadService, UploadService>();
        services.AddSingleton<IUploadExpiryService, UploadExpiryService>();
        services.AddSingleton<IAccessKeyService, AccessKeyService>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IBlobSweepService, BlobSweepService>();
        services.AddSingleton<ICredentialStore, DatabaseCredentialStore>();
        return services;
    }
}
