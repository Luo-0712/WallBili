namespace WallBili.Core;

/// <summary>Global quality preference of the caller. A null tier id selects automatic mode:
/// the highest available video tier and the highest normal audio tier.</summary>
public sealed record MediaSelectionPreferences(string? VideoQualityId = null, string? AudioQualityId = null)
{
    public static MediaSelectionPreferences Automatic { get; } = new();
}

/// <summary>A stable quality tier identity plus display metadata. Never contains media addresses.</summary>
public sealed record MediaQualityOption(string Id, string Label, bool Special = false);

/// <summary>What the current media session actually offers and plays. Contains no URLs or credentials,
/// so it is safe for snapshots, UI display and diagnostics.</summary>
public sealed record MediaSelectionSummary(
    IReadOnlyList<MediaQualityOption> VideoOptions,
    IReadOnlyList<MediaQualityOption> AudioOptions,
    string? SelectedVideo = null,
    string? SelectedAudio = null,
    string? VideoFallback = null,
    string? AudioFallback = null,
    bool EmbeddedAudio = false,
    bool NoAudio = false);
