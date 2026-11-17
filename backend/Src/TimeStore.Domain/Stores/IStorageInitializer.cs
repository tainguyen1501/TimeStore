namespace TimeStore.Domain.Stores;

/// <summary>Startup work of a provider (schema migration, directories). The host runs every one before serving.</summary>
public interface IStorageInitializer
{
    ValueTask InitializeAsync(CancellationToken ct);
}
