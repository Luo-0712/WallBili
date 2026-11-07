using System.Text.Json;

namespace WallBili.AgentCli;

/// <summary>Only the two preferences this launcher depends on. Unknown fields are ignored and a
/// missing or malformed file means "unknown", never a failed launch.</summary>
public sealed record AgentSettings(bool? McpEnabled, int? McpPort)
{
    public static AgentSettings Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "preferences.json");
            if (!File.Exists(path)) return new(null, null);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var enabled = root.TryGetProperty("McpEnabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? e.GetBoolean()
                : (bool?)null;
            var port = root.TryGetProperty("McpPort", out var p) && p.TryGetInt32(out var value) && value is >= 1 and <= 65535
                ? value
                : (int?)null;
            return new(enabled, port);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null);
        }
    }
}

/// <summary>Written by the desktop application so an assistant can find the launcher and the app
/// even while the application is not running. It never contains the access token.</summary>
public sealed record AgentManifest(string? AppPath, string? CliPath, string? DataDir, string? Endpoint)
{
    public const string FileName = "agent.json";

    public static AgentManifest Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path)) return new(null, null, null, null);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new(Text(root, "appPath"), Text(root, "cliPath"), Text(root, "dataDir"), Text(root, "endpoint"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null, null, null);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;
}
