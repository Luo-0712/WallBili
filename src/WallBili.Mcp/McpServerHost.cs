using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using WallBili.Core;

namespace WallBili.Mcp;

/// <summary>Owns the loopback-only MCP listener. Application state stays in the supplied control.</summary>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int DefaultPort = 38291;
    public const int MaximumRequestBodyBytes = 1024 * 1024;
    public const int MaximumConcurrentRequests = 8;

    private readonly WebApplication _application;
    private readonly SemaphoreSlim _requests;
    private int _disposed;

    private McpServerHost(WebApplication application, Uri endpoint, SemaphoreSlim requests)
    {
        _application = application;
        Endpoint = endpoint;
        _requests = requests;
    }

    public Uri Endpoint { get; }

    public static async Task<McpServerHost> StartAsync(IWallBiliControl control, int port, string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var requests = new SemaphoreSlim(MaximumConcurrentRequests);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(McpServerHost).Assembly.GetName().Name
        });
        // Do not inherit ASPNETCORE_URLS or Kestrel endpoint configuration from the desktop process.
        builder.Configuration.Sources.Clear();
        // This host is embedded in a desktop process. In particular, never log HTTP headers or tool inputs.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "WallBili", Version = "1.0.0" };
                options.ServerInstructions = "Control the running WallBili application. Search and inspect content before creating a playlist. " +
                    "Queue edits append by default; set autoplay explicitly only when the user asks to play. " +
                    "After requesting playback, call get_status and check actual output before reporting that playback has started.";
            })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithListToolsHandler(async (_, _) => new ListToolsResult
            {
                Tools = control.Tools.Select(ToProtocolTool).ToList()
            })
            .WithCallToolHandler(async (request, cancellation) =>
            {
                var parameters = request.Params;
                if (parameters is null || !control.Tools.Any(tool => tool.Name == parameters.Name))
                    return ToProtocolResult(AgentResult.Error("unknown_tool", "Unknown WallBili tool.", "Read tools/list and use an advertised tool name."));
                try
                {
                    var arguments = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>(), AgentJson.Options);
                    return ToProtocolResult(await control.InvokeAsync(parameters.Name, arguments, cancellation).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Low-level handlers must not echo unexpected exceptions (which can contain upstream credentials).
                    return ToProtocolResult(AgentResult.Error("internal_error", "WallBili could not complete this request.",
                        "Read get_status and retry with the same requestId if the operation supports it."));
                }
            });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!ValidHost(context.Request.Host, port) || !ValidOrigin(context.Request, endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!ValidToken(context.Request, expectedTokenHash))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            if (context.Request.ContentLength > MaximumRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            if (!await requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "1";
                return;
            }
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            context.RequestAborted = stopping.Token;
            try { await next(context).ConfigureAwait(false); }
            finally { requests.Release(); }
        });
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new McpServerHost(app, endpoint, requests);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            requests.Dispose();
            throw;
        }
    }

    private static bool ValidHost(HostString host, int port) =>
        string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal) && (host.Port ?? 80) == port;

    private static bool ValidOrigin(HttpRequest request, Uri endpoint)
    {
        if (!request.Headers.TryGetValue("Origin", out var values)) return true;
        return values.Count == 1 && Uri.TryCreate(values[0], UriKind.Absolute, out var origin) &&
               origin.Scheme == endpoint.Scheme && origin.Host == endpoint.Host && origin.Port == endpoint.Port &&
               origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    private static bool ValidToken(HttpRequest request, byte[] expectedHash)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values) || values.Count != 1) return false;
        var value = values[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(value.AsSpan(7).ToString()));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static Tool ToProtocolTool(AgentToolDefinition definition) => new()
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = definition.InputSchema,
        OutputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true }),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = definition.ReadOnly,
            DestructiveHint = definition.Destructive,
            OpenWorldHint = true
        }
    };

    private static CallToolResult ToProtocolResult(AgentResult result) => new()
    {
        Content = [new TextContentBlock { Text = result.Summary }],
        StructuredContent = result.Data.Clone(),
        IsError = result.IsError
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _application.StopAsync().ConfigureAwait(false); }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
            _requests.Dispose();
        }
    }
}
