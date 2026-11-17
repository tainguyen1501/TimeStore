using Microsoft.Data.Sqlite;

namespace TimeStore.Metadata.Sqlite;

/// <summary>
/// Connections to the metadata database: pooled read connections (no per-open statement: reads need no pragma), and
/// the single write connection of <see cref="SqliteWriter"/>, whose pragmas run once when it opens.
/// </summary>
public sealed class SqliteConnectionFactory(SqliteMetadataOptions options)
{
    // synchronous=FULL: a committed object survives power loss (group commit pays one fsync per round).
    // foreign_keys: enforced on writes. busy_timeout: the writer only waits on a checkpoint or the migrator.
    private const string WriterPragmas =
        "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";

    private readonly string _readers = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    private readonly string _writer = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false, // owned for the whole process lifetime by the writer
    }.ToString();

    public string DatabasePath => options.DatabasePath;

    /// <summary>A pooled connection for reads (and the schema migrator, which runs before the writer starts).</summary>
    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_readers);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The write connection, with its pragmas applied once.</summary>
    public async ValueTask<SqliteConnection> OpenWriterAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_writer);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = WriterPragmas;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
