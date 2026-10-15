namespace WallBili.Player.Mpv;

public sealed record MpvPlayerOptions
{
    public string ExecutablePath { get; init; } = Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv";
    public bool Headless { get; init; }
    public bool StartPaused { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(45);
}
