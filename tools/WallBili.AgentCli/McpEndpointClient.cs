using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WallBili.AgentCli;

/// <summary>Transport or authentication failure that should be reported with a stable code.</summary>
public sealed class McpException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record ToolInfo(string Name, string? Description, bool ReadOnly, bool Destructive);

public sealed record ToolCallOutcome(string Summary, JsonElement? Data, bool IsError,
    string? Code, string? Message, string? NextStep);

/// <summary>Minimal Streamable HTTP JSON-RPC client. Only loopback is used, and credentials are
/// never forwarded to a proxy or a redirect target.</summary>
public sealed class McpEndpointClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Func<string?> _token;
    private readonly object _gate = new();
    private string? _sessionId;
    private bool _initialized;
    private int _nextId;

    public McpEndpointClient(Uri endpoint, Func<string?> token, TimeSpan requestTimeout)
    {
        _endpoint = endpoint;
        _token = token;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = requestTimeout
        };
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        _ = await SendRequestAsync("tools/list", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync("tools/list", new Dictionary<string, object?>(), cancellationToken)
            .ConfigureAwait(false);
        var tools = new List<ToolInfo>();
        if (!result.TryGetProperty("tools", out var list) || list.ValueKind != JsonValueKind.Array) return tools;
        foreach (var tool in list.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var description = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
            var readOnly = tool.TryGetProperty("annotations", out var annotations) &&
                           annotations.TryGetProperty("readOnlyHint", out var ro) && ro.ValueKind == JsonValueKind.True;
            var destructive = tool.TryGetProperty("annotations", out var second) &&
                              second.TryGetProperty("destructiveHint", out var de) && de.ValueKind == JsonValueKind.True;
            tools.Add(new(name, description, readOnly, destructive));
        }
        return tools;
    }

    public async Task<ToolCallOutcome> CallToolAsync(string name, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync("tools/call", new { name, arguments }, cancellationToken)
            .ConfigureAwait(false);
        var isError = result.TryGetProperty("isError", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True;
        var summary = ReadSummary(result);
        JsonElement? data = result.TryGetProperty("structuredContent", out var structured) &&
                            structured.ValueKind == JsonValueKind.Object
            ? structured.Clone()
            : null;
        string? code = null, message = null, nextStep = null;
        if (data is { } value && value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = Text(error, "code");
            message = Text(error, "message");
            nextStep = Text(error, "nextStep");
        }
        return new(summary, data, isError, code, message, nextStep);
    }

    private static string ReadSummary(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                block.TryGetProperty("text", out var text) && text.GetString() is { } value)
                lines.Add(value);
        return string.Join("\n", lines);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { if (_initialized) return; }
        await SendRequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new Dictionary<string, object?>(),
            clientInfo = new { name = "WallBili.AgentCli", version = "1.0.0" }
        }, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", new Dictionary<string, object?>(), cancellationToken)
            .ConfigureAwait(false);
        lock (_gate) { _initialized = true; }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, Json);
        var messages = await PostAsync(payload, cancellationToken).ConfigureAwait(false);
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id)
                continue;
            if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw new McpException("rpc_error", Text(error, "message") ?? "The MCP endpoint rejected the request.");
            return message.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
        throw new McpException("invalid_response", "The MCP endpoint did not return a matching response.");
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters }, Json);
        _ = await PostAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JsonElement>> PostAsync(byte[] payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var token = _token();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        string? sessionId;
        lock (_gate) { sessionId = _sessionId; }
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("timeout", "The MCP endpoint did not respond in time.");
        }
        catch (HttpRequestException exception)
        {
            throw new McpException("unavailable", "The MCP endpoint is not reachable.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new McpException("token_invalid", "The MCP endpoint rejected the access token.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new McpException("forbidden", "The MCP endpoint rejected the request origin.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new McpException("rate_limited", "The MCP endpoint is busy; retry shortly.");
            if (!response.IsSuccessStatusCode)
                throw new McpException("http_error", $"The MCP endpoint returned HTTP {(int)response.StatusCode}.");

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values) && values.FirstOrDefault() is { Length: > 0 } id)
                lock (_gate) { _sessionId = id; }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return ParseBody(body, contentType);
        }
    }

    private static IReadOnlyList<JsonElement> ParseBody(string body, string? mediaType)
    {
        var messages = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(body)) return messages;
        if (mediaType is not null && mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (var frame in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var data = string.Join("\n", frame.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].TrimStart()));
                if (data.Length == 0) continue;
                using var document = JsonDocument.Parse(data);
                messages.Add(document.RootElement.Clone());
            }
            return messages;
        }
        using var single = JsonDocument.Parse(body);
        messages.Add(single.RootElement.Clone());
        return messages;
    }

    public void Dispose() => _http.Dispose();
}
