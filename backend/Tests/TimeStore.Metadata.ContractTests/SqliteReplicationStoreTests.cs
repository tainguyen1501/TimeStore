using Microsoft.Data.Sqlite;
using TimeStore.Domain.Stores;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Metadata.ContractTests;

/// <summary>The replication contract on SQLite: a fresh database file per test.</summary>
public sealed class SqliteReplicationStoreTests : ReplicationStoreContract
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));

    protected override async ValueTask<(IMetadataStore Store, IReplicationStore Replication)> CreateAsync()
    {
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(Ct);
        var store = new SqliteMetadataStore(connections);
        return (store, store);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Store is IAsyncDisposable store)
            await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
