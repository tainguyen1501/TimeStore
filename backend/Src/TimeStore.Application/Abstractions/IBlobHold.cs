namespace TimeStore.Application.Abstractions;

/// <summary>
/// Keeps released blobs on disk while a backup copies the blobs of its snapshot: a blob the snapshot references and a
/// commit releases afterwards is only deleted once every hold has ended. Writes and reads are not affected.
/// </summary>
/// <remarks>
/// Every committed blob is deleted through <see cref="IBlobDeletions"/>, and the orphan sweep skips blobs waiting
/// there, so holding that queue is enough: nothing else deletes a blob a snapshot may reference.
/// </remarks>
public interface IBlobHold
{
    /// <summary>Takes a hold; disposing it releases the hold, and deletions that waited run once none is left.</summary>
    IAsyncDisposable Hold();
}
