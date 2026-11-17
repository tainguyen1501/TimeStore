namespace TimeStore.Metadata.Sqlite;

/// <param name="DatabasePath">Path of the SQLite file (created when missing).</param>
public sealed record SqliteMetadataOptions(string DatabasePath);
