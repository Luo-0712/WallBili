using System.Text.Json;
using WallBili.AgentCli;
using Xunit;

namespace WallBili.AgentCli.Tests;

public sealed class McpEndpointClientTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsToolsAndReturnsStructuredResults()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        await environment.StartServerAsync(port);
        using var client = new McpEndpointClient(new Uri($"http://127.0.0.1:{port}/mcp"),
            () => TestEnvironment.Token, TimeSpan.FromSeconds(5));

        var tools = await client.ListToolsAsync(Cancellation);
        var tool = Assert.Single(tools);
        Assert.Equal("get_status", tool.Name);
        Assert.True(tool.ReadOnly);

        var outcome = await client.CallToolAsync("get_status",
            JsonSerializer.SerializeToElement(new { }), Cancellation);
        Assert.False(outcome.IsError);
        Assert.Equal("Playback is active.", outcome.Summary);
        Assert.True(outcome.Data!.Value.GetProperty("outputActive").GetBoolean());
    }

    [Fact]
    public async Task WrongTokenIsReportedAsTokenInvalid()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        await environment.StartServerAsync(port);
        using var client = new McpEndpointClient(new Uri($"http://127.0.0.1:{port}/mcp"),
            () => "wrong-token-0123456789012345678901234567", TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ProbeAsync(Cancellation));
        Assert.Equal("token_invalid", exception.Code);
    }

    [Fact]
    public async Task UnreachableEndpointIsReportedAsUnavailable()
    {
        var port = TestEnvironment.AvailablePort();
        using var client = new McpEndpointClient(new Uri($"http://127.0.0.1:{port}/mcp"),
            () => TestEnvironment.Token, TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ProbeAsync(Cancellation));
        Assert.Equal("unavailable", exception.Code);
    }
}
