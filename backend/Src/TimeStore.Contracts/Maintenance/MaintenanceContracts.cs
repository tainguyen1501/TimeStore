namespace TimeStore.Contracts.Maintenance;

/// <summary>One orphan-blob sweep.</summary>
/// <param name="Scanned">Blobs old enough to be candidates (younger ones are skipped, not counted).</param>
/// <param name="Deleted">Candidates no metadata row referenced, now removed.</param>
public sealed record BlobSweepResponse(int Scanned, int Deleted, long DeletedBytes, DateTime StartedAt, DateTime FinishedAt);
