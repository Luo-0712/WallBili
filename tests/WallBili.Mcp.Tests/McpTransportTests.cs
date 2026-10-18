using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WallBili.Core;
using WallBili.Mcp;
using Xunit;

namespace WallBili.Mcp.Tests;

public sealed class McpTransportTests
{
    private const string Token = "test-only-token-01234567890123456789";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RejectsUnauthenticatedRequestAndReleasesPortOnDispose()
    {
        var port = AvailablePort();
        var server = await McpServerHost.StartAsync(new FakeControl(), port, Token, Cancellation);
        using var http = new HttpClient();
        using var response = await http.PostAsync(server.Endpoint,
            new StringContent("{}", Encoding.UTF8, "application/json"), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await server.DisposeAsync();
        await using var restarted = await McpServerHost.StartAsync(new FakeControl(), port, Token, Cancellation);
        Assert.Equal(new Uri($"http://127.0.0.1:{port}/mcp"), restarted.Endpoint);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task OfficialClientDiscoversSchemasAndInvokesStructuredToolsAcrossProtocolRevisions(string revision)
    {
        var control = new FakeControl();
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint, revision);
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
        Assert.Equal(1, control.Calls);
    }

    [Fact]
    public async Task BusinessErrorsRemainStructuredAndUnexpectedExceptionsAreSanitized()
    {
        var control = new FakeControl
        {
            Handler = (_, _, _) => Task.FromResult(AgentResult.Error("login_required", "Sign in first.", "Sign in from WallBili settings."))
        };
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint);
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation);
        Assert.True(result.IsError);
        Assert.Equal("login_required", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        control.Handler = (_, _, _) => throw new InvalidOperationException("SESSDATA=secret&signed_url=secret");
        result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation);
        Assert.True(result.IsError);
        Assert.Equal("internal_error", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task UnknownToolDoesNotReachApplicationControl()
    {
        var control = new FakeControl();
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint);
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = "nonexistent" }, Cancellation);
        Assert.True(result.IsError);
        Assert.Equal(0, control.Calls);
    }

    [Fact]
    public async Task PreservesNestedContentReferencesAndRequestIdWithoutResolvingMedia()
    {
        JsonElement forwarded = default;
        var control = new FakeControl { Handler = (_, arguments, _) =>
        {
            forwarded = arguments.Clone();
            return Task.FromResult(AgentResult.Success("Accepted.", new { taskId = "task-1", playlistId = "list-1" }));
        }};
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"requestId":"request-1","content":{"kind":"video","bvid":"BV1234567890"},"autoplay":false}
            """)!;
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status", Arguments = arguments }, Cancellation);
        Assert.Equal("request-1", forwarded.GetProperty("requestId").GetString());
        Assert.Equal("BV1234567890", forwarded.GetProperty("content").GetProperty("bvid").GetString());
        Assert.False(forwarded.GetProperty("autoplay").GetBoolean());
        Assert.Equal("task-1", result.StructuredContent!.Value.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task StoppingHostCancelsActiveRequestsAndCanBeDisposedTwice()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new FakeControl { Handler = async (_, _, cancellation) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
            finally { if (cancellation.IsCancellationRequested) cancelled.TrySetResult(); }
            return AgentResult.Success("Done.", new { });
        }};
        var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint);
        var pending = client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            await server.DisposeAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), Cancellation);
            await server.DisposeAsync();
        }
        finally
        {
            await server.DisposeAsync();
            try { await pending; }
            catch (Exception) { /* SDK reports a cancelled stream or a closed HTTP response. */ }
        }
    }

    [Theory]
    [InlineData(null, null, null, 401)]
    [InlineData("wrong-token", null, null, 401)]
    [InlineData(Token, "attacker.invalid", null, 403)]
    [InlineData(Token, "127.0.0.1:1", null, 403)]
    [InlineData(Token, null, "https://attacker.invalid", 403)]
    [InlineData(Token, null, "null", 403)]
    [InlineData(Token, null, "http://127.0.0.1:1", 403)]
    [InlineData(Token, null, "http://user@127.0.0.1:38291", 403)]
    public async Task RejectsInvalidAccessBoundary(string? token, string? host, string? origin, int expectedStatus)
    {
        var control = new FakeControl();
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        using var http = new HttpClient();
        using var request = Request(server.Endpoint, token);
        if (host is not null) request.Headers.Host = host;
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await http.SendAsync(request, Cancellation);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Server"));
        Assert.Equal(0, control.Calls);
    }

    [Fact]
    public async Task AcceptsSameOriginWithoutEnablingCors()
    {
        await using var server = await McpServerHost.StartAsync(new FakeControl(), AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint, origin: server.Endpoint.GetLeftPart(UriPartial.Authority));
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task RejectsMultipleOriginValuesAndDuplicateAuthorization()
    {
        await using var server = await McpServerHost.StartAsync(new FakeControl(), AvailablePort(), Token, Cancellation);
        using var http = new HttpClient();
        using var request = Request(server.Endpoint);
        request.Headers.TryAddWithoutValidation("Origin", [server.Endpoint.GetLeftPart(UriPartial.Authority), "https://attacker.invalid"]);
        using var response = await http.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var duplicateAuthorization = Request(server.Endpoint);
        duplicateAuthorization.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        using var duplicateResponse = await http.SendAsync(duplicateAuthorization, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task RejectsOversizedBodiesBeforeToolExecution()
    {
        var control = new FakeControl();
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        using var http = new HttpClient();
        using var request = Request(server.Endpoint);
        request.Content = new StringContent(new string('x', McpServerHost.MaximumRequestBodyBytes + 1), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, control.Calls);
    }

    [Fact]
    public async Task RejectsExcessConcurrentRequestsWithoutQueueingUnboundedWork()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var control = new FakeControl { Handler = async (_, _, cancellation) =>
        {
            if (Interlocked.Increment(ref active) == McpServerHost.MaximumConcurrentRequests) allStarted.SetResult();
            await release.Task.WaitAsync(cancellation);
            return AgentResult.Success("Done.", new { done = true });
        }};
        await using var server = await McpServerHost.StartAsync(control, AvailablePort(), Token, Cancellation);
        await using var client = await ConnectAsync(server.Endpoint);
        var pending = Enumerable.Range(0, McpServerHost.MaximumConcurrentRequests)
            .Select(_ => client.CallToolAsync(new CallToolRequestParams { Name = "get_status" }, Cancellation).AsTask()).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            using var http = new HttpClient();
            using var request = Request(server.Endpoint);
            using var response = await http.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("1", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending); }
    }

    [Fact]
    public async Task PortConflictIsReportedWithoutStoppingExistingServer()
    {
        var port = AvailablePort();
        await using var server = await McpServerHost.StartAsync(new FakeControl(), port, Token, Cancellation);
        await Assert.ThrowsAsync<IOException>(() => McpServerHost.StartAsync(new FakeControl(), port, Token, Cancellation));
        await using var client = await ConnectAsync(server.Endpoint);
        Assert.Single((await client.ListToolsAsync(new ListToolsRequestParams(), Cancellation)).Tools);
    }

    internal static Task<McpClient> ConnectAsync(Uri endpoint, string? revision = null, string? origin = null)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" };
        if (origin is not null) headers["Origin"] = origin;
        return McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, AdditionalHeaders = headers,
            EnableStandaloneGetStream = false
        }), new McpClientOptions { ProtocolVersion = revision, InitializationTimeout = TimeSpan.FromSeconds(10) }, cancellationToken: Cancellation);
    }

    private static HttpRequestMessage Request(Uri endpoint, string? token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal sealed class FakeControl : IWallBiliControl
    {
        public int Calls;
        public Func<string, JsonElement, CancellationToken, Task<AgentResult>>? Handler { get; set; }
        public IReadOnlyList<AgentToolDefinition> Tools { get; } =
        [new("get_status", "Read current playback status.", JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { }, additionalProperties = false
        }), ReadOnly: true)];

        public Task<AgentResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Handler?.Invoke(name, arguments, cancellationToken) ??
                Task.FromResult(AgentResult.Success("Playback is active.", new { outputActive = true, title = "First lesson" }));
        }
    }
}
