using WallBili.Core;

namespace WallBili.AgentCli;

/// <summary>Resolved locations and credentials for one launcher invocation.</summary>
public sealed class AgentContext
{
    public const string DataDirectoryVariable = "WALLBILI_DATA_DIR";
    public const string AppPathVariable = "WALLBILI_APP_PATH";

    private AgentContext(string dataDirectory, bool customDataDirectory, string? requestedAppPath,
        int port, bool portWasExplicit, Uri endpoint, TokenProvider tokens, AgentManifest manifest)
    {
        DataDirectory = dataDirectory;
        CustomDataDirectory = customDataDirectory;
        RequestedAppPath = requestedAppPath;
        Port = port;
        PortWasExplicit = portWasExplicit;
        Endpoint = endpoint;
        Tokens = tokens;
        Manifest = manifest;
    }

    public string DataDirectory { get; }
    public bool CustomDataDirectory { get; }
    public string? RequestedAppPath { get; }
    public int Port { get; }
    public bool PortWasExplicit { get; }
    public Uri Endpoint { get; }
    public TokenProvider Tokens { get; }
    public AgentManifest Manifest { get; }

    /// <summary>Preferences are read per call so a settings change made while polling is observed.</summary>
    public AgentSettings ReadSettings() => AgentSettings.Read(DataDirectory);

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallBili");

    public static AgentContext Create(CliOptions options)
    {
        var defaultDirectory = Path.GetFullPath(DefaultDataDirectory);
        var dataDirectory = options.DataDirectory
            ?? Environment.GetEnvironmentVariable(DataDirectoryVariable)
            ?? defaultDirectory;
        dataDirectory = Path.GetFullPath(dataDirectory);
        var custom = !string.Equals(dataDirectory.TrimEnd('\\'), defaultDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var manifest = AgentManifest.Read(dataDirectory);
        var settings = AgentSettings.Read(dataDirectory);
        var requestedApp = options.AppPath ?? Environment.GetEnvironmentVariable(AppPathVariable);
        var port = options.Endpoint?.Port ?? options.Port ?? settings.McpPort ?? AgentMcpEndpoint.DefaultPort;
        var endpoint = options.Endpoint ?? new Uri($"http://127.0.0.1:{port}/mcp");
        return new(dataDirectory, custom, requestedApp, endpoint.Port, options.Endpoint is not null || options.Port is not null,
            endpoint, new TokenProvider(options.Token, dataDirectory), manifest);
    }

    /// <summary>The explicit path wins even when it is missing so the error can name it; otherwise
    /// the manifest and the packaged/direct layout next to this executable are tried.</summary>
    public string? ResolveAppPath()
    {
        if (RequestedAppPath is { Length: > 0 }) return File.Exists(RequestedAppPath) ? Path.GetFullPath(RequestedAppPath) : null;
        if (Manifest.AppPath is { Length: > 0 } recorded && File.Exists(recorded)) return Path.GetFullPath(recorded);

        var baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "WallBili.Desktop.exe"),
            Path.Combine(baseDirectory, "app", "WallBili.Desktop.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "app", "WallBili.Desktop.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "app", "WallBili.Desktop.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "WallBili.Desktop.exe"))
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
