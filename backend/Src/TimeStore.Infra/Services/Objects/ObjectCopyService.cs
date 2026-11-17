using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Cryptography.Checksums;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Stores;
using TimeStore.Primitives;

namespace TimeStore.Infra.Services.Objects;

public sealed class ObjectCopyService(
    IMetadataStore store, IBlobStore blobs, IBlobDeletions deletions, IChecksumFactory checksums, TimeProvider clock)
    : IObjectCopyService
{
    public async ValueTask<Result<CopyObjectResult>> CopyAsync(
        CopyObjectCommand command, ObjectReader source, CancellationToken ct)
    {
        var from = source.Object;
        if (from.Size > ObjectErrors.MaxCopySourceBytes)
            return ObjectErrors.CopySourceTooLarge;

        var metadata = command.ReplaceMetadata ? command.UserMetadata : from.UserMetadata;
        if (ObjectValidation.ValidateKeyAndMetadata(command.Key, metadata) is { } invalid)
            return invalid;
        if (await store.FindBucketAsync(command.Bucket, ct) is not { } bucket)
            return BucketErrors.NotFound(command.Bucket);
        // Copying the current version onto itself changes nothing; copying an older version over its key restores it.
        if (bucket.Id == from.BucketId && command.Key == from.Key && !command.ReplaceMetadata
            && await store.FindObjectAsync(bucket.Id, command.Key, ct) is { } current && current.VersionId == from.VersionId)
            return ObjectErrors.CopyToItself;

        var algorithmName = command.ChecksumAlgorithm ?? from.ChecksumAlgorithm;
        using var checksum = algorithmName is not null && ChecksumAlgorithms.TryParse(algorithmName, out var algorithm)
            ? checksums.Create(algorithm)
            : null;
        var content = checksum is null ? source.Content : new ChecksummingReadStream(source.Content, checksum);

        // The copy is a new blob written like any PUT (durable before the commit), so the source can go away afterwards.
        var blob = await blobs.WriteAsync(content, ct);
        var committed = false;
        try
        {
            if (blob.Size != from.Size)
                throw new IOException($"Copied {blob.Size} bytes of a {from.Size}-byte source.");

            ObjectChecksum? computed = null;
            if (checksum is not null)
            {
                var digest = new byte[checksum.Length];
                checksum.GetHashAndReset(digest);
                computed = new ObjectChecksum(ChecksumAlgorithms.NameOf(checksum.Algorithm), digest);
            }

            var copy = new StoredObject(
                bucket.Id, command.Key, Guid.CreateVersion7(), new SingleBlob(blob), blob.Md5Hex,
                command.ReplaceMetadata ? command.ContentType ?? StoredObject.DefaultContentType : from.ContentType,
                computed?.Algorithm, computed?.Digest, ChecksumType: null,
                command.ReplaceMetadata ? command.Headers : from.Headers, metadata, clock.UtcNowMs);
            var commit = await store.CommitObjectAsync(
                copy, command.OnlyIfAbsent ? CommitCondition.IfAbsent : CommitCondition.Always, ct);
            switch (commit.Status)
            {
                case CommitStatus.BucketMissing:
                    return BucketErrors.NotFound(command.Bucket);
                case CommitStatus.ConditionFailed:
                    return ObjectErrors.AlreadyExists(command.Key);
            }

            committed = true;
            if (commit.Replaced is { } replaced)
                await deletions.DeferAsync(replaced.Blobs);
            return new CopyObjectResult(copy.ETag, copy.CreatedAt, computed, commit.Version?.WriteHeader);
        }
        finally
        {
            if (!committed)
                await blobs.DeleteQuietlyAsync(blob);
        }
    }
}
