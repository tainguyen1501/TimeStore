using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimeStore.Domain.Stores;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Metadata.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    /// <summary>Registers the SQLite metadata provider. Called only by the host (composition root).</summary>
    public static IServiceCollection AddSqliteMetadataStore(this IServiceCollection services, SqliteMetadataOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IMetadataStore, SqliteMetadataStore>();
        services.AddSingleton<IStorageInitializer, SqliteMetadataInitializer>();
        return services;
    }
}
