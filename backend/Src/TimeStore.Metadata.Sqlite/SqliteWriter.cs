using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// The only writer of the database: one connection, one queue, group commit. SQLite allows one writer at a time; with
/// a connection per request, concurrent writes collided on the write lock and slept in the busy handler (second-long
/// tails), and every commit paid its own fsync. Here each round takes every queued write, runs each in its own
/// <c>SAVEPOINT</c> inside one transaction, commits once (one fsync, <c>synchronous=FULL</c>), and only then hands each
/// caller its result: an acknowledged write is durable.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A write that throws is rolled back to its savepoint; the others in the round still commit. A failed commit
/// fails every write of the round.</item>
/// <item>Once queued a write always runs to the end: a caller that gave up must not believe the write did not happen
/// (it could delete a blob the committed row points to). Writes therefore ignore the caller's cancellation.</item>
/// <item>Reads do not come here: they use pooled connections, which WAL lets run beside the writer.</item>
/// </list>
/// </remarks>
public sealed class SqliteWriter : IAsyncDisposable
{
    /// <summary>Writes per transaction at most; a longer queue is committed in several rounds.</summary>
    public const int MaxBatch = 256;

    private readonly SqliteConnectionFactory _connections;
    private readonly Channel<Work> _queue =
        Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _loop;
    private long _batches;

    public SqliteWriter(SqliteConnectionFactory connections)
    {
        _connections = connections;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Transactions committed so far (tests and diagnostics: proves writes are grouped).</summary>
    public long CommittedBatches => Interlocked.Read(ref _batches);

    /// <summary>
    /// Queues <paramref name="write"/> and returns its result once the transaction holding it has committed. The
    /// delegate gets the writer's connection and transaction (every command must use that transaction).
    /// </summary>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, ValueTask<T>> write)
    {
        var work = new Work<T>(write);
        if (!_queue.Writer.TryWrite(work))
            throw new ObjectDisposedException(nameof(SqliteWriter));
        return work.Completion.Task;
    }

    private async Task RunAsync()
    {
        SqliteConnection? connection = null;
        var round = new List<Work>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                while (round.Count < MaxBatch && _queue.Reader.TryRead(out var work))
                    round.Add(work);

                try
                {
                    connection ??= await _connections.OpenWriterAsync(CancellationToken.None);
                    await RunRoundAsync(connection, round);
                }
                catch (Exception failure)
                {
                    // No transaction could start or commit: every write of the round failed. Start over with a fresh
                    // connection, in case this one is broken.
                    foreach (var work in round)
                        work.Fail(failure);
                    if (connection is not null)
                        await connection.DisposeAsync();
                    connection = null;
                }

                round.Clear();
            }
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }
    }

    private async Task RunRoundAsync(SqliteConnection connection, List<Work> round)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(); // IMMEDIATE
        for (var i = 0; i < round.Count; i++)
        {
            var savepoint = Savepoints[i];
            await transaction.SaveAsync(savepoint);
            try
            {
                await round[i].RunAsync(connection, transaction);
                await transaction.ReleaseAsync(savepoint);
            }
            catch (Exception failure)
            {
                round[i].Defer(failure);
                await transaction.RollbackAsync(savepoint);
                await transaction.ReleaseAsync(savepoint);
            }
        }

        await transaction.CommitAsync();
        Interlocked.Increment(ref _batches);
        foreach (var work in round)
            work.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _loop; // drains what is queued
    }

    private static readonly string[] Savepoints = [.. Enumerable.Range(0, MaxBatch).Select(i => $"w{i}")];

    private abstract class Work
    {
        public abstract ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction);

        /// <summary>Remembers a failure of this write; reported after the commit (the round may still succeed).</summary>
        public abstract void Defer(Exception failure);

        /// <summary>The commit succeeded: report this write's result or its deferred failure.</summary>
        public abstract void Complete();

        /// <summary>The round could not commit: this write did not happen.</summary>
        public abstract void Fail(Exception failure);
    }

    private sealed class Work<T>(Func<SqliteConnection, SqliteTransaction, ValueTask<T>> write) : Work
    {
        private T? _result;
        private Exception? _failure;

        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction) =>
            _result = await write(connection, transaction);

        public override void Defer(Exception failure) => _failure = failure;

        public override void Complete()
        {
            if (_failure is not null)
                Completion.TrySetException(_failure);
            else
                Completion.TrySetResult(_result!);
        }

        public override void Fail(Exception failure) => Completion.TrySetException(failure);
    }
}
