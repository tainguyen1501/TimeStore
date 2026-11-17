using Microsoft.Data.Sqlite;

namespace TimeStore.Metadata.Sqlite;

internal static class SqliteParameters
{
    extension(SqliteParameterCollection parameters)
    {
        /// <summary>
        /// Binds a Guid as a 16-byte **big-endian** BLOB (RFC 9562 byte order), so UUIDv7 primary keys sort by time
        /// and keep index locality. Microsoft.Data.Sqlite binds <see cref="Guid"/> as TEXT by default, which a STRICT
        /// BLOB column rejects. Read back with <see cref="SqliteValues.GetId"/>.
        /// </summary>
        public void AddId(string name, Guid id) =>
            parameters.Add(name, SqliteType.Blob).Value = id.ToByteArray(bigEndian: true);

        public void AddText(string name, string value) => parameters.Add(name, SqliteType.Text).Value = value;

        public void AddInt64(string name, long value) => parameters.Add(name, SqliteType.Integer).Value = value;

        public void AddNullableText(string name, string? value) =>
            parameters.Add(name, SqliteType.Text).Value = (object?)value ?? DBNull.Value;

        public void AddNullableInt64(string name, long? value) =>
            parameters.Add(name, SqliteType.Integer).Value = (object?)value ?? DBNull.Value;

        public void AddNullableBlob(string name, byte[]? value) =>
            parameters.Add(name, SqliteType.Blob).Value = (object?)value ?? DBNull.Value;
    }
}

internal static class SqliteValues
{
    private const int GuidLength = 16;

    extension(SqliteDataReader reader)
    {
        /// <summary>Reads a Guid stored by <see cref="SqliteParameters.AddId"/> (big-endian BLOB).</summary>
        public Guid GetId(int ordinal)
        {
            var bytes = reader.GetFieldValue<byte[]>(ordinal);
            return bytes.Length == GuidLength
                ? new Guid(bytes, bigEndian: true)
                : throw new InvalidDataException($"Column {ordinal} is not a {GuidLength}-byte id.");
        }
    }
}
