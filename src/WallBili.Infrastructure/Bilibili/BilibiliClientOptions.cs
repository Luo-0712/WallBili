namespace WallBili.Infrastructure.Bilibili;

public sealed record BilibiliClientOptions
{
    /// <summary>Request a single MP4 first; fall back to DASH if that representation is unavailable.</summary>
    public bool PreferProgressiveMp4 { get; init; } = true;
}
