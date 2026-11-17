using TimeStore.Domain.Blobs;

namespace TimeStore.Domain.Objects;

/// <summary>A bucket's versioning state (Amazon S3): never enabled, enabled, or suspended after being enabled.</summary>
public enum BucketVersioning
{
    Unversioned,
    Enabled,
    Suspended,
}

/// <summary>
/// A version a client names: the <c>null</c> version or one internal id. Internal ids are UUIDv7 and order versions by
/// time; the null version has its own internal id too (<see cref="StoredObject.VersionId"/>).
/// </summary>
public readonly record struct VersionRef(Guid Id, bool IsNull)
{
    public static VersionRef Null => new(Guid.Empty, true);

    public static VersionRef Of(Guid id) => new(id, false);
}

/// <summary>
/// Versions of a key are ordered by internal id (UUIDv7, big-endian). UUIDv7 has millisecond precision and random
/// low bits, so two writes in the same millisecond could sort the wrong way: a new version takes
/// <see cref="Next"/> of the newest existing id.
/// </summary>
public static class VersionOrder
{
    /// <summary><paramref name="candidate"/> when it sorts after <paramref name="newest"/>, else newest + 1 (low bits).</summary>
    public static Guid Next(Guid candidate, Guid? newest)
    {
        if (newest is not { } latest || Compare(candidate, latest) > 0)
            return candidate;

        Span<byte> bytes = stackalloc byte[16];
        latest.TryWriteBytes(bytes, bigEndian: true, out _);
        // Bytes 9-15 are random (byte 8 keeps the variant bits): add one with carry.
        for (var i = 15; i >= 9; i--)
        {
            if (++bytes[i] != 0)
                return new Guid(bytes, bigEndian: true);
        }

        throw new InvalidOperationException("Version id space of one millisecond exhausted.");
    }

    public static int Compare(Guid a, Guid b)
    {
        Span<byte> x = stackalloc byte[16];
        Span<byte> y = stackalloc byte[16];
        a.TryWriteBytes(x, bigEndian: true, out _);
        b.TryWriteBytes(y, bigEndian: true, out _);
        return x.SequenceCompareTo(y);
    }
}

/// <summary>Client-visible version ids: 32 lowercase hex characters of the internal id, or <c>null</c>.</summary>
public static class VersionIds
{
    public const string Null = "null";
    private const string Format32 = "N";

    public static string Format(Guid id, bool isNull) => isNull ? Null : id.ToString(Format32);

    /// <summary>False for anything that is neither <c>null</c> nor 32 hex characters.</summary>
    public static bool TryParse(string? text, out VersionRef version)
    {
        version = default;
        if (text == Null)
        {
            version = VersionRef.Null;
            return true;
        }

        if (text is not { Length: 32 } || !Guid.TryParseExact(text, Format32, out var id))
            return false;
        version = VersionRef.Of(id);
        return true;
    }
}

/// <summary>The version a write produced, and the bucket state that decided it.</summary>
public sealed record ObjectVersion(Guid Id, bool IsNull, BucketVersioning Versioning)
{
    /// <summary>The <c>x-amz-version-id</c> naming this version (a delete marker), or null in a never-versioned bucket.</summary>
    public string? Header => Versioning == BucketVersioning.Unversioned ? null : VersionIds.Format(Id, IsNull);

    /// <summary>
    /// The <c>x-amz-version-id</c> of a write (PUT, copy, completed upload): only when versioning is enabled. A write
    /// in a suspended bucket answers no version id (Amazon S3 and ceph s3-tests agree).
    /// </summary>
    public string? WriteHeader => Versioning == BucketVersioning.Enabled ? VersionIds.Format(Id, IsNull) : null;
}

/// <summary>A delete request for one key: the latest (adds a marker when versioned) or one version.</summary>
public sealed record ObjectToDelete(string Key, VersionRef? Version);

/// <summary>
/// What a delete did. <paramref name="Released"/>: data no version references any more (delete its blobs).
/// <paramref name="Version"/>: the marker created, or the version removed (null when nothing matched in an
/// unversioned bucket). <paramref name="IsDeleteMarker"/>: a marker was created or removed.
/// </summary>
public sealed record ObjectDeletion(string Key, ObjectData? Released, ObjectVersion? Version, bool IsDeleteMarker);

/// <summary>One entry of ListObjectVersions: an object version or a delete marker.</summary>
/// <param name="SupersededAt">When the version became noncurrent (null for the latest).</param>
public sealed record VersionSummary(
    string Key, Guid VersionId, bool IsNullVersion, bool IsDeleteMarker, bool IsLatest, long Size, string ETag, DateTime CreatedAt,
    DateTime? SupersededAt = null)
{
    public string VersionIdText => VersionIds.Format(VersionId, IsNullVersion);
}

/// <param name="NextKeyMarker">With <paramref name="NextVersionIdMarker"/>: where the next page starts (truncated only).</param>
public sealed record VersionPage(
    IReadOnlyList<VersionSummary> Versions,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextVersionIdMarker);

/// <summary>A version looked up by id, or the latest when it is a delete marker.</summary>
public closed record VersionLookup;

public sealed record FoundVersion(StoredObject Object, bool IsLatest) : VersionLookup;

public sealed record FoundDeleteMarker(Guid VersionId, bool IsNullVersion, DateTime CreatedAt, bool IsLatest) : VersionLookup
{
    public string VersionIdText => VersionIds.Format(VersionId, IsNullVersion);
}

public sealed record VersionMissing : VersionLookup
{
    public static readonly VersionMissing Instance = new();
}

/// <summary>Blobs and results of a multi-object delete (one transaction).</summary>
public sealed record DeletionBatch(IReadOnlyList<ObjectDeletion> Results)
{
    public IEnumerable<BlobLocator> ReleasedBlobs => Results.SelectMany(static r => r.Released?.Blobs ?? []);
}
