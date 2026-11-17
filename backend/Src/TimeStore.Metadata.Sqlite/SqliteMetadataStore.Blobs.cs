using System.Text;

namespace TimeStore.Metadata.Sqlite;

public sealed partial class SqliteMetadataStore
{
    // Well below SQLite's bound-parameter limit (32 766); one index probe per id (ix_objects_blob).
    private const int BlobBatchSize = 500;
    private const string BlobParameterPrefix = "$b";

    public async ValueTask<IReadOnlyList<Guid>> FindUnreferencedBlobsAsync(IReadOnlyList<Guid> blobIds, CancellationToken ct)
    {
        if (blobIds.Count == 0)
            return [];

        var referenced = new HashSet<Guid>();
        await using var connection = await connections.OpenAsync(ct);
        for (var start = 0; start < blobIds.Count; start += BlobBatchSize)
        {
            var count = Math.Min(BlobBatchSize, blobIds.Count - start);
            await using var command = connection.CreateCommand();
            var list = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var name = BlobParameterPrefix + i; // parameter names only: no value ever enters the SQL text
                list.Append(i == 0 ? "" : ", ").Append(name);
                command.Parameters.AddId(name, blobIds[start + i]);
            }

            var ids = list.ToString();
            // Every table that references blobs (one index probe per id in each).
            command.CommandText = $"SELECT blob_id FROM objects WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM object_parts WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM object_versions WHERE blob_id IN ({ids}) "
                + $"UNION SELECT blob_id FROM upload_parts WHERE blob_id IN ({ids});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                referenced.Add(reader.GetId(0));
        }

        var unreferenced = new List<Guid>();
        foreach (var id in blobIds)
        {
            if (!referenced.Contains(id))
                unreferenced.Add(id);
        }

        return unreferenced;
    }
}
