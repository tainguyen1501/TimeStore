using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Objects;

/// <summary>
/// Where an object's bytes are. Closed: every consumer (read, overwrite, delete, sweep) must handle both shapes, so a
/// new shape breaks the build until it is handled everywhere.
/// </summary>
public closed record ObjectData
{
    public long Size => this switch
    {
        SingleBlob single => single.Blob.Size,
        MultipartBlobs multipart => multipart.Parts.Count == 0 ? 0 : multipart.Parts[^1].Offset + multipart.Parts[^1].Blob.Size,
    };

    /// <summary>Every blob holding this object's bytes (to delete when the object goes away).</summary>
    public IReadOnlyList<BlobLocator> Blobs => this switch
    {
        SingleBlob single => [single.Blob],
        MultipartBlobs multipart => multipart.Parts.Select(p => p.Blob).ToArray(),
    };
}

/// <summary>A single PUT: one blob.</summary>
public sealed record SingleBlob(BlobLocator Blob) : ObjectData;

/// <summary>
/// A completed multipart upload: a manifest of part blobs in part-number order, never copied into one blob.
/// </summary>
/// <param name="UploadId">The upload that produced it (a retried Complete returns the stored result).</param>
/// <param name="Parts">Ordered by part number; offsets are contiguous from 0.</param>
public sealed record MultipartBlobs(Guid UploadId, IReadOnlyList<ObjectPart> Parts) : ObjectData
{
    /// <summary>The part that holds byte <paramref name="position"/> (binary search over offsets).</summary>
    public int IndexOfPartAt(long position)
    {
        int low = 0, high = Parts.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Parts[middle].Offset <= position)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }
}

/// <summary>One part of a multipart object.</summary>
/// <param name="Offset">Position of the part's first byte in the object.</param>
/// <param name="Checksum">The part's checksum (the object's algorithm), canonical byte order, or null.</param>
public sealed record ObjectPart(int PartNumber, long Offset, BlobLocator Blob, byte[]? Checksum)
{
    /// <summary>A part's ETag is the hex MD5 of its blob, without quotes.</summary>
    public string ETag => Blob.Md5Hex;
}

/// <summary>What a listing shows about an object (no blob locations: listings never read part manifests).</summary>
public sealed record ObjectSummary(string Key, long Size, string ETag, DateTime CreatedAt);
