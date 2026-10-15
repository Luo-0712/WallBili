using System.IO;
using System.Text.Json;

namespace WallBili.Desktop;

internal sealed record DesktopPreferences
{
    public string MpvPath { get; init; } = Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv";
    public bool WallpaperEnabled { get; init; } = true;
    public string? DisplayId { get; init; }
    public bool FillWindow { get; init; } = true;
    public bool ContinueAudioWhenHidden { get; init; } = true;

    public static DesktopPreferences Read(string path)
    {
        try { return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
