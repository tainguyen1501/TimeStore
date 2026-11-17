using Microsoft.Data.Sqlite;
using TimeStore.Metadata.Sqlite;

namespace TimeStore.Metadata.ContractTests;

/// <summary>
/// The single writer: grouping, isolation between the writes of one round, recovery. A first write that waits on a gate
/// holds its round open while the others queue, so the next round's content is known.
/// </summary>
public sealed class SqliteWriterTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _connections = null!;
    private SqliteWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "w.db")));
        await using (var connection = await _connections.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE t (v INTEGER NOT NULL PRIMARY KEY) STRICT;";
            await command.ExecuteNonQueryAsync();
        }

        _writer = new SqliteWriter(_connections);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<int> Insert(int value, bool thenFail = false) =>
        _writer.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO t (v) VALUES ({value});";
            await command.ExecuteNonQueryAsync();
            if (thenFail)
                throw new InvalidOperationException($"write {value} fails after inserting");
            return value;
        });

    private async Task<long[]> RowsAsync()
    {
        await using var connection = await _connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t ORDER BY v;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<long>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetInt64(0));
        return [.. rows];
    }

    /// <summary>A write that holds its round open until <paramref name="gate"/> completes.</summary>
    private Task<int> Held(Task gate) => _writer.WriteAsync(async (_, _) =>
    {
        await gate;
        return 0;
    });

    [Fact]
    public async Task WritesQueuedMeanwhile_CommitTogether_InOneTransaction()
    {
        var gate = new TaskCompletionSource();
        var held = Held(gate.Task);
        await Task.Delay(50, TestContext.Current.CancellationToken); // let the first round start with only the held write
        var queued = Enumerable.Range(1, 100).Select(i => Insert(i)).ToArray();
        var before = _writer.CommittedBatches;

        gate.SetResult();
        await held;
        var results = await Task.WhenAll(queued);

        Assert.Equal(Enumerable.Range(1, 100), results);
        Assert.Equal(2, _writer.CommittedBatches - before); // the held round, then one round for all 100
        Assert.Equal(100, (await RowsAsync()).Length);
    }

    [Fact]
    public async Task AFailingWrite_IsRolledBackAlone_ItsNeighboursCommit()
    {
        var gate = new TaskCompletionSource();
        var held = Held(gate.Task);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var first = Insert(1);
        var failing = Insert(2, thenFail: true);
        var last = Insert(3);

        gate.SetResult();
        await held;

        Assert.Equal(1, await first);
        Assert.Equal(3, await last);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Contains("write 2", error.Message);
        Assert.Equal([1L, 3L], await RowsAsync()); // write 2's row went with its savepoint
    }

    [Fact]
    public async Task AConstraintViolation_FailsOnlyThatWrite_AndTheWriterGoesOn()
    {
        await Insert(7);

        await Assert.ThrowsAsync<SqliteException>(() => Insert(7)); // primary key
        Assert.Equal(8, await Insert(8));
        Assert.Equal([7L, 8L], await RowsAsync());
    }

    [Fact]
    public async Task Dispose_DrainsTheQueue()
    {
        var writes = Enumerable.Range(10, 20).Select(i => Insert(i)).ToArray();

        await _writer.DisposeAsync();

        Assert.All(writes, w => Assert.True(w.IsCompletedSuccessfully));
        Assert.Equal(20, (await RowsAsync()).Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Insert(99));
        _writer = new SqliteWriter(_connections); // for DisposeAsync of the test
    }
}
