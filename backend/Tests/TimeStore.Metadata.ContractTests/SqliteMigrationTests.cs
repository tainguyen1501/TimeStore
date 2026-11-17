using Microsoft.Data.Sqlite;
using TimeStore.Domain.Objects;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;

namespace TimeStore.Metadata.ContractTests;

/// <summary>Upgrades of an existing database: data written under an older schema survives every later migration.</summary>
public sealed class SqliteMigrationTests : IDisposable
{
    private const int MultipartVersion = 4;
    private const int BucketUsageVersion = 6;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task V4_RebuildsObjects_KeepingEveryRow()
    {
        Directory.CreateDirectory(_directory);
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        var bucketId = Guid.CreateVersion7();
        var blobId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();

        await using (var connection = await connections.OpenAsync(_ct))
        {
            await ExecuteAsync(connection, SqliteMigrations.CreateHistory);
            for (var version = 1; version < MultipartVersion; version++)
            {
                await ExecuteAsync(connection, SqliteMigrations.All[version - 1]);
                await ExecuteAsync(connection, $"INSERT INTO schema_migrations (version, applied_at) VALUES ({version}, 0);");
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO buckets (id, name, created_at) VALUES ($bucket, 'old', 1);
                INSERT INTO objects (bucket_id, key, version_id, blob_id, node_id, size, etag, content_type,
                                     checksum_algorithm, checksum, headers, user_metadata, created_at)
                VALUES ($bucket, 'a.txt', $version, $blob, 'node-1', 3, '900150983cd24fb0d6963f7d28e17f72', 'text/plain',
                        'CRC32', x'01020304', 'cache-control:no-cache' || char(10), 'owner:felix' || char(10), 1763285415123);
                """;
            insert.Parameters.AddId("$bucket", bucketId);
            insert.Parameters.AddId("$version", versionId);
            insert.Parameters.AddId("$blob", blobId);
            await insert.ExecuteNonQueryAsync(_ct);
        }

        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(_ct);

        await using var store = new SqliteMetadataStore(connections);
        var found = await store.FindObjectAsync(bucketId, "a.txt", _ct);
        Assert.NotNull(found);
        Assert.Equal(versionId, found.VersionId);
        var blob = Assert.IsType<SingleBlob>(found.Data).Blob;
        Assert.Equal((blobId, "node-1", 3L), (blob.BlobId, blob.NodeId, blob.Size));
        Assert.Equal("CRC32", found.ChecksumAlgorithm);
        Assert.Equal([1, 2, 3, 4], found.Checksum);
        Assert.Null(found.ChecksumType);
        Assert.Equal([new ObjectHeader("cache-control", "no-cache")], found.Headers);
        Assert.Equal([new ObjectHeader("owner", "felix")], found.UserMetadata);
    }

    [Fact]
    public async Task V6_BackfillsBucketUsage_FromExistingObjects()
    {
        Directory.CreateDirectory(_directory);
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        var full = Guid.CreateVersion7();
        var empty = Guid.CreateVersion7();

        await using (var connection = await connections.OpenAsync(_ct))
        {
            await ExecuteAsync(connection, SqliteMigrations.CreateHistory);
            for (var version = 1; version < BucketUsageVersion; version++)
            {
                await ExecuteAsync(connection, SqliteMigrations.All[version - 1]);
                await ExecuteAsync(connection, $"INSERT INTO schema_migrations (version, applied_at) VALUES ({version}, 0);");
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO buckets (id, name, created_at) VALUES ($full, 'full', 1), ($empty, 'empty', 1);
                INSERT INTO objects (bucket_id, key, version_id, blob_id, node_id, size, etag, content_type, created_at)
                VALUES ($full, 'a', randomblob(16), randomblob(16), 'node-1', 3, 'x', 'text/plain', 1),
                       ($full, 'b', randomblob(16), randomblob(16), 'node-1', 7, 'x', 'text/plain', 1);
                """;
            insert.Parameters.AddId("$full", full);
            insert.Parameters.AddId("$empty", empty);
            await insert.ExecuteNonQueryAsync(_ct);
        }

        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(_ct);

        await using var store = new SqliteMetadataStore(connections);
        var usage = await store.ListBucketUsageAsync(_ct);
        Assert.Equal([("empty", 0L, 0L), ("full", 2L, 10L)], usage.Select(u => (u.BucketName, u.ObjectCount, u.TotalBytes)));
    }

    private async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(_ct);
    }
}
