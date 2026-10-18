using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WallBili.McpBridge;

/// <summary>A transport adapter only; the running desktop app owns every business operation.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            await Console.Error.WriteLineAsync("WallBili.McpBridge [--endpoint http://127.0.0.1:38291/mcp]\n" +
                "Set WALLBILI_MCP_TOKEN to the token copied from WallBili settings. WallBili must already be running.");
            return 0;
        }
        BridgeOptions options;
        try { options = BridgeOptions.Parse(args, Environment.GetEnvironmentVariable("WALLBILI_MCP_TOKEN")); }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            // Never forward credentials to a proxy, another host, or a redirect target.
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            await using var upstream = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = options.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.Token}" },
                EnableStandaloneGetStream = false,
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            }, http), new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(8) }, cancellationToken: stopping.Token);

            var serverOptions = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "WallBili.McpBridge", Version = "1.0.0" },
                ServerInstructions = upstream.ServerInstructions,
                ScopeRequests = false,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (request, cancellation) => upstream.ListToolsAsync(request.Params ?? new ListToolsRequestParams(), cancellation),
                    CallToolHandler = async (request, cancellation) =>
                    {
                        try
                        {
                            return await upstream.CallToolAsync(request.Params ?? throw new McpException("Tool parameters are required."), cancellation);
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                        catch (Exception)
                        {
                            // Never retry mutations implicitly or expose network exception text on the wire.
                            return new CallToolResult
                            {
                                IsError = true,
                                Content = [new TextContentBlock { Text = "The running WallBili application could not be reached. Check its MCP settings, then read get_status before retrying." }],
                                StructuredContent = JsonSerializer.SerializeToElement(new
                                {
                                    error = new { code = "application_unavailable", message = "WallBili is unavailable.", nextStep = "Open WallBili and verify that MCP is enabled; retry mutations with the same requestId." }
                                })
                            };
                        }
                    }
                }
            };
            await using var server = McpServer.Create(new StdioServerTransport(serverOptions), serverOptions);
            await server.RunAsync(stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Cannot connect to WallBili. Start the application, enable MCP, and check the endpoint and WALLBILI_MCP_TOKEN.");
            return 3;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}

public sealed record BridgeOptions(Uri Endpoint, string Token)
{
    public static BridgeOptions Parse(string[] args, string? token)
    {
        var value = "http://127.0.0.1:38291/mcp";
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] is not ("--endpoint" or "--url"))
                throw new ArgumentException("Use --endpoint http://127.0.0.1:PORT/mcp. Supply the token through WALLBILI_MCP_TOKEN.");
            value = args[1];
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            endpoint.Host != "127.0.0.1" || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/mcp" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("The endpoint must be http://127.0.0.1:PORT/mcp without credentials, query, or fragment.");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("WALLBILI_MCP_TOKEN is required. Copy the connection settings from WallBili.");
        if (token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("WALLBILI_MCP_TOKEN must not contain line breaks.");
        return new BridgeOptions(endpoint, token);
    }
}
