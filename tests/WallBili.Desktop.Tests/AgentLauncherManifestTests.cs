using System.IO;
using System.Text.Json;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class AgentLauncherManifestTests
{
    [Fact]
    public void ManifestRecordsPathsAndNeverTheToken()
    {
        var directory = NewDirectory();
        var app = Path.Combine(directory, "app", "WallBili.Desktop.exe");
        var cli = Path.Combine(directory, "agent-cli", "WallBili.AgentCli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(app, string.Empty);
        File.WriteAllText(cli, string.Empty);

        AgentLauncherManifest.Write(directory, app, cli, new Uri("http://127.0.0.1:38291/mcp"), true, 38291);

        var text = File.ReadAllText(Path.Combine(directory, AgentLauncherManifest.FileName));
        Assert.False(text.Contains("token", StringComparison.OrdinalIgnoreCase));
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(app, root.GetProperty("appPath").GetString());
        Assert.Equal(cli, root.GetProperty("cliPath").GetString());
        Assert.Equal("http://127.0.0.1:38291/mcp", root.GetProperty("endpoint").GetString());
        Assert.True(root.GetProperty("mcpEnabled").GetBoolean());
        Assert.Equal(38291, root.GetProperty("port").GetInt32());
    }

    [Fact]
    public void MissingCliLeavesANullPathButKeepsTheApplication()
    {
        var directory = NewDirectory();
        var app = Path.Combine(directory, "app", "WallBili.Desktop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, string.Empty);

        AgentLauncherManifest.Write(directory, app, AgentLauncherManifest.FindCliPath(app),
            new Uri("http://127.0.0.1:38291/mcp"), false, 38291);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "agent.json")));
        Assert.False(document.RootElement.TryGetProperty("cliPath", out var cli) && cli.ValueKind != JsonValueKind.Null);
        Assert.False(document.RootElement.GetProperty("mcpEnabled").GetBoolean());
    }

    [Fact]
    public void CliIsDiscoveredNextToTheApplication()
    {
        var directory = NewDirectory();
        var app = Path.Combine(directory, "app", "WallBili.Desktop.exe");
        var cli = Path.Combine(directory, "agent-cli", "WallBili.AgentCli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(app, string.Empty);
        File.WriteAllText(cli, string.Empty);

        Assert.Equal(Path.GetFullPath(cli), AgentLauncherManifest.FindCliPath(app));
    }

    [Fact]
    public void MissingApplicationIsTolerated()
    {
        Assert.Null(AgentLauncherManifest.FindCliPath(null));
        Assert.Null(AgentLauncherManifest.FindCliPath(Path.Combine(NewDirectory(), "agent", "WallBili.Desktop.exe")));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wallbili-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
