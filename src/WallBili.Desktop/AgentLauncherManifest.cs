using System.IO;
using System.Text.Json;
using WallBili.Core;

namespace WallBili.Desktop;

/// <summary>Lets an assistant find the launcher and the application while WallBili is not running.
/// The access token is deliberately not part of this file.</summary>
internal static class AgentLauncherManifest
{
    internal const string FileName = "agent.json";

    internal static void Write(string dataDirectory, string? appPath, string? cliPath, Uri endpoint,
        bool mcpEnabled, int port)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                appPath,
                cliPath,
                dataDir = dataDirectory,
                endpoint = endpoint.AbsoluteUri,
                mcpEnabled,
                port
            }, AgentJson.Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Discovery metadata must never block startup.
        }
    }

    /// <summary>Packaged builds put the launcher next to the app; development builds keep it under
    /// the repository's tools directory and simply leave this null.</summary>
    internal static string? FindCliPath(string? appPath)
    {
        if (appPath is not { Length: > 0 }) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(appPath));
        if (directory is null) return null;
        string[] candidates =
        [
            Path.Combine(directory, "WallBili.AgentCli.exe"),
            Path.Combine(directory, "agent-cli", "WallBili.AgentCli.exe"),
            Path.GetFullPath(Path.Combine(directory, "..", "agent-cli", "WallBili.AgentCli.exe")),
            Path.GetFullPath(Path.Combine(directory, "..", "..", "agent-cli", "WallBili.AgentCli.exe"))
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
