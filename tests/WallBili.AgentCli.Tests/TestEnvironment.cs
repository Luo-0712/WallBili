using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WallBili.AgentCli;
using WallBili.Core;
using WallBili.Mcp;
using Xunit;
using Xunit.v3;

[assembly: Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace WallBili.AgentCli.Tests;

internal sealed class TestEnvironment : IAsyncDisposable
{
    internal const string Token = "test-only-token-01234567890123456789";
    private readonly List<McpServerHost> _servers = [];

    internal TestEnvironment()
    {
        Directory = Path.Combine(Path.GetTempPath(), "wallbili-agent-cli-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        AppPath = Path.Combine(Directory, "WallBili.Desktop.exe");
        File.WriteAllText(AppPath, string.Empty);
        PreviousToken = Environment.GetEnvironmentVariable(TokenProvider.EnvironmentVariable);
        Environment.SetEnvironmentVariable(TokenProvider.EnvironmentVariable, null);
    }

    internal string Directory { get; }
    internal string AppPath { get; }
    private string? PreviousToken { get; }

    internal string PreferencesPath => Path.Combine(Directory, "preferences.json");

    internal void WritePreferences(bool enabled, int port) =>
        File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new { McpEnabled = enabled, McpPort = port, MpvPath = "mpv" }));

    internal void WriteManifest(string appPath) =>
        File.WriteAllText(Path.Combine(Directory, AgentManifest.FileName),
            JsonSerializer.Serialize(new { appPath, cliPath = (string?)null, dataDir = Directory, endpoint = (string?)null }));

    internal async Task<McpServerHost> StartServerAsync(int port, string token = Token)
    {
        var server = await McpServerHost.StartAsync(new FakeControl(), port, token);
        _servers.Add(server);
        return server;
    }

    internal static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal CliOptions Options(int port, string? token = Token, string? app = null, double timeout = 5,
        bool noLaunch = false) => new()
    {
        Command = CliCommand.Ensure,
        DataDirectory = Directory,
        AppPath = app,
        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
        Token = token,
        Timeout = TimeSpan.FromSeconds(timeout),
        NoLaunch = noLaunch
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync();
        Environment.SetEnvironmentVariable(TokenProvider.EnvironmentVariable, PreviousToken);
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}

internal sealed class FakeControl : IWallBiliControl
{
    public IReadOnlyList<AgentToolDefinition> Tools { get; } =
    [
        new("get_status", "Read current playback status.", JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { }, additionalProperties = false
        }), ReadOnly: true)
    ];

    public Task<AgentResult> InvokeAsync(string name, JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AgentResult.Success("Playback is active.", new { outputActive = true, title = "First lesson" }));
}
