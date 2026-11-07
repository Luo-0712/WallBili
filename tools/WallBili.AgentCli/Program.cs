using System.Text;
using System.Text.Json;

namespace WallBili.AgentCli;

public static class Program
{
    private static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CliOptions options;
        try { options = CliOptions.Parse(args); }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(CliOptions.Usage).ConfigureAwait(false);
            return 2;
        }
        if (options.Help)
        {
            await Console.Out.WriteLineAsync(CliOptions.Usage).ConfigureAwait(false);
            return 0;
        }

        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var context = AgentContext.Create(options);
            var ensure = new EnsureService(context, new AgentProcessController());
            return options.Command switch
            {
                CliCommand.Ensure => await RunEnsureAsync(ensure, options, stopping.Token).ConfigureAwait(false),
                CliCommand.List => await RunListAsync(context, ensure, options, stopping.Token).ConfigureAwait(false),
                CliCommand.Call => await RunCallAsync(context, ensure, options, stopping.Token).ConfigureAwait(false),
                _ => 2
            };
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 3; }
        catch (Exception)
        {
            // Never echo exception text: it can include request details or credentials.
            await WriteAsync(new
            {
                ok = false,
                error = new
                {
                    code = "internal_error",
                    message = "WallBili.AgentCli could not complete the request.",
                    nextStep = "Retry once; if it persists, run with --no-launch and check WallBili manually."
                }
            }).ConfigureAwait(false);
            return 3;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<int> RunEnsureAsync(EnsureService ensure, CliOptions options,
        CancellationToken cancellationToken)
    {
        var result = await ensure.EnsureAsync(!options.NoLaunch, options.Timeout, cancellationToken)
            .ConfigureAwait(false);
        await WriteAsync(result).ConfigureAwait(false);
        return result.Ok ? 0 : 3;
    }

    private static async Task<int> RunListAsync(AgentContext context, EnsureService ensure, CliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.EnsureTools)
        {
            var ready = await ensure.EnsureAsync(!options.NoLaunch, options.Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!ready.Ok)
            {
                await WriteAsync(ready).ConfigureAwait(false);
                return 3;
            }
        }
        using var client = new McpEndpointClient(context.Endpoint, context.Tokens.Resolve, TimeSpan.FromSeconds(30));
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(new { ok = true, tools }).ConfigureAwait(false);
            return 0;
        }
        catch (McpException exception)
        {
            await WriteAsync(ErrorResult(exception)).ConfigureAwait(false);
            return 3;
        }
    }

    private static async Task<int> RunCallAsync(AgentContext context, EnsureService ensure, CliOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.NoEnsure)
        {
            var ready = await ensure.EnsureAsync(!options.NoLaunch, options.Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!ready.Ok)
            {
                await WriteAsync(ready).ConfigureAwait(false);
                return 3;
            }
        }
        if (context.Tokens.Resolve() is null)
        {
            await WriteAsync(new
            {
                ok = false,
                error = new
                {
                    code = "token_unavailable",
                    message = "The MCP access token is not readable.",
                    nextStep = "Run ensure first, or start WallBili and enable MCP in 助手连接."
                }
            }).ConfigureAwait(false);
            return 3;
        }

        JsonElement arguments;
        try { arguments = ReadArguments(options.ArgumentsJson); }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("Arguments must be a JSON object, @file, or - for stdin.")
                .ConfigureAwait(false);
            return 2;
        }

        using var client = new McpEndpointClient(context.Endpoint, context.Tokens.Resolve, TimeSpan.FromSeconds(90));
        try
        {
            var outcome = await client.CallToolAsync(options.ToolName!, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.IsError)
            {
                await WriteAsync(new { ok = true, summary = outcome.Summary, data = outcome.Data })
                    .ConfigureAwait(false);
                return 0;
            }
            await WriteAsync(new
            {
                ok = false,
                error = new
                {
                    code = outcome.Code ?? "tool_error",
                    message = outcome.Message ?? outcome.Summary,
                    nextStep = outcome.NextStep ?? "Read get_status and adjust the request before retrying."
                }
            }).ConfigureAwait(false);
            return 1;
        }
        catch (McpException exception)
        {
            await WriteAsync(ErrorResult(exception)).ConfigureAwait(false);
            return 3;
        }
    }

    private static JsonElement ReadArguments(string? value)
    {
        string json;
        if (value is null or "{}") json = "{}";
        else if (value == "-") json = Console.In.ReadToEnd();
        else if (value.StartsWith('@')) json = File.ReadAllText(value[1..]);
        else json = value;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The arguments must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static object ErrorResult(McpException exception) => new
    {
        ok = false,
        error = new
        {
            code = exception.Code,
            message = exception.Message,
            nextStep = NextStep(exception.Code)
        }
    };

    private static string NextStep(string code) => code switch
    {
        "unavailable" => "Run ensure to start WallBili, then retry.",
        "token_invalid" => "Copy the MCP configuration again from 助手连接, then retry.",
        "forbidden" => "Use the exact loopback endpoint from 助手连接.",
        "timeout" => "Check playback state with get_status before retrying.",
        "rate_limited" => "Wait a moment before retrying.",
        _ => "Read get_status, then retry once with the same requestId."
    };

    private static async Task WriteAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Output)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
