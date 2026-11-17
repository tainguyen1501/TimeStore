using Microsoft.Data.Sqlite;
using TimeStore.Domain.Stores;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Metadata.ContractTests;

/// <summary>The contract on SQLite: a fresh database file per test.</summary>
public sealed class SqliteMetadataStoreTests : MetadataStoreContract
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _connections = null!;

    protected override async ValueTask<IMetadataStore> CreateStoreAsync()
    {
        _connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        await ReinitializeAsync();
        return new SqliteMetadataStore(_connections);
    }

    protected override ValueTask ReinitializeAsync() =>
        new SqliteMetadataInitializer(_connections, TimeProvider.System).InitializeAsync(Ct);

    public override async ValueTask DisposeAsync()
    {
        if (Store is IAsyncDisposable store)
            await store.DisposeAsync(); // the writer closes its connection
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
