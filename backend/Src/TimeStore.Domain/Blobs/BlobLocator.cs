namespace TimeStore.Domain.Blobs;

/// <summary>
/// Where a blob lives and what it contains. Blobs are immutable: overwriting an object writes a new blob and moves
/// the metadata pointer. Metadata stores this locator, never a file path, so replicas can be added later
/// (multi-node: more locations per blob) without migrating data.
/// </summary>
public sealed record BlobLocator(Guid BlobId, string NodeId, long Size, string Md5Hex)
{
    /// <summary>Node ids: lowercase letters, digits and hyphens, at most this long.</summary>
    public const int NodeIdMaxLength = 64;

    /// <summary>MD5 as lowercase hex.</summary>
    public const int Md5HexLength = 32;
}
