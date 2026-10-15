namespace WallBili.Core;

public enum ContentFailureKind
{
    InvalidInput,
    Inaccessible,
    Network,
    Removed,
    AuthenticationRequired,
    Unsupported,
    RateLimited,
    InvalidResponse
}

/// <summary>Safe, actionable content failure without signed URLs, headers or server response bodies.</summary>
public sealed class ContentAccessException(ContentFailureKind kind, string message, int? serviceCode = null)
    : Exception(message)
{
    public ContentFailureKind Kind { get; } = kind;
    public int? ServiceCode { get; } = serviceCode;
}
