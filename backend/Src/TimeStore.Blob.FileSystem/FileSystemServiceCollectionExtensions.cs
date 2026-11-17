using Microsoft.Extensions.DependencyInjection;
using TimeStore.Domain.Stores;

namespace TimeStore.Blob.FileSystem;

public static class FileSystemServiceCollectionExtensions
{
    /// <summary>Registers the file-system blob provider. Called only by the host (composition root).</summary>
    public static IServiceCollection AddFileSystemBlobStore(this IServiceCollection services, FileSystemBlobOptions options)
    {
        var store = new FileSystemBlobStore(options);
        services.AddSingleton<IBlobStore>(store);
        services.AddSingleton<IStorageInitializer>(store);
        return services;
    }
}
