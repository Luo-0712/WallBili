using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WallBili.Core;
using WallBili.McpBridge;
using Xunit;

namespace WallBili.Mcp.Tests;

public sealed class McpBridgeTests
{
    private const string Token = "test-only-token-01234567890123456789";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StdioBridgePreservesToolsSchemasAnnotationsAndResultsAndDoesNotStopDesktop()
    {
        var control = new McpTransportTests.FakeControl();
        await using var server = await McpServerHost.StartAsync(control, McpTransportTests.AvailablePort(), Token, Cancellation);
        var errors = new ConcurrentQueue<string>();
        await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = Dotnet(), Arguments = [BridgeAssembly(), "--endpoint", server.Endpoint.AbsoluteUri],
            EnvironmentVariables = new Dictionary<string, string?> { ["WALLBILI_MCP_TOKEN"] = Token },
            StandardErrorLines = line => errors.Enqueue(line), ShutdownTimeout = TimeSpan.FromSeconds(5)
        }), new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(10) }, cancellationToken: Cancellation))
        {
            var tools = await client.ListToolsAsync(new ListToolsRequestParams(), Cancellation);
            var tool = Assert.Single(tools.Tools);
            Assert.Equal("get_status", tool.Name);
            Assert.True(tool.Annotations!.ReadOnlyHint);
            Assert.False(tool.Annotations.DestructiveHint);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.Equal("object", tool.OutputSchema!.Value.GetProperty("type").GetString());
            var result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation);
            Assert.False(result.IsError);
            Assert.Equal("Playback is active.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(result.StructuredContent!.Value.GetProperty("outputActive").GetBoolean());
            control.Handler = (_, _, _) => Task.FromResult(AgentResult.Error("login_required", "Sign in first.", "Use WallBili settings."));
            result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation);
            Assert.True(result.IsError);
            Assert.Equal("login_required", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        }
        await using var direct = await McpTransportTests.ConnectAsync(server.Endpoint);
        Assert.Single((await direct.ListToolsAsync(new ListToolsRequestParams(), Cancellation)).Tools);
        Assert.DoesNotContain(Token, string.Join('\n', errors));
    }

    [Fact]
    public async Task InvalidCliConfigurationExitsWithoutProtocolNoiseOrLeakingToken()
    {
        var result = await RunAsync(["--endpoint", "https://example.com/mcp"], Token);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("127.0.0.1", result.Stderr);
        Assert.DoesNotContain(Token, result.Stderr);
    }

    [Fact]
    public async Task MissingTokenExitsWithActionableStderrOnly()
    {
        var result = await RunAsync([], null);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("WALLBILI_MCP_TOKEN is required", result.Stderr);
    }

    [Fact]
    public async Task StoppedApplicationDoesNotColdStartAndReportsConnectionFailure()
    {
        var result = await RunAsync(["--url", $"http://127.0.0.1:{McpTransportTests.AvailablePort()}/mcp"], Token);
        Assert.Equal(3, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Start the application", result.Stderr);
        Assert.DoesNotContain(Token, result.Stderr);
    }

    [Theory]
    [InlineData("http://attacker.invalid:38291/mcp")]
    [InlineData("http://127.0.0.1:38291/mcp?token=secret")]
    [InlineData("http://user:secret@127.0.0.1:38291/mcp")]
    [InlineData("http://127.0.0.1:38291/other")]
    public void RefusesRemoteOrAmbiguousEndpoint(string endpoint) =>
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--endpoint", endpoint], Token));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] arguments, string? token)
    {
        var start = new ProcessStartInfo(Dotnet())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(BridgeAssembly());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (token is null) start.Environment.Remove("WALLBILI_MCP_TOKEN");
        else start.Environment["WALLBILI_MCP_TOKEN"] = token;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(Cancellation);
        var stderr = process.StandardError.ReadToEndAsync(Cancellation);
        await process.WaitForExitAsync(Cancellation).WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string BridgeAssembly()
    {
        return Path.Combine(AppContext.BaseDirectory, "mcp-bridge", "WallBili.McpBridge.dll");
    }

    private static string Dotnet()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var runtimeRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}
