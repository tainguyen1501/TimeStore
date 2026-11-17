namespace TimeStore.Contracts.Common;

/// <summary>Admin API error body. <see cref="Code"/> is the contract; <see cref="TraceId"/> matches the logs.</summary>
public sealed record ErrorResponse(string Code, string Message, string TraceId);

public sealed record HealthResponse(string Status, string NodeId)
{
    public const string Healthy = "healthy";
}
