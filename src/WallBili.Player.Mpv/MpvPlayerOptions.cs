namespace WallBili.Player.Mpv;

public sealed record MpvPlayerOptions
{
    public string ExecutablePath { get; init; } = Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv";
    public bool Headless { get; init; }
    public bool StartPaused { get; init; }
    /// <summary>Optional HWND created and owned by WallBili. Desktop binding is an opt-in Windows prototype.</summary>
    public long? WindowId { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(45);
}
