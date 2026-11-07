using System.Diagnostics;

namespace WallBili.AgentCli;

public sealed record EnsureResult(bool Ok, bool Launched, string Endpoint, string? AppPath, long ElapsedMs,
    string? Code = null, string? Message = null, string? NextStep = null)
{
    public static EnsureResult Success(bool launched, string endpoint, string? appPath, long elapsedMs) =>
        new(true, launched, endpoint, appPath, elapsedMs);

    public static EnsureResult Failure(string code, string message, string nextStep, string endpoint,
        string? appPath, long elapsedMs) =>
        new(false, false, endpoint, appPath, elapsedMs, code, message, nextStep);
}

/// <summary>Starts the application only when it is not already running and waits for its loopback
/// MCP endpoint to answer an authenticated request. Every failure carries a stable code and a
/// concrete next step so the assistant can relay it without guessing.</summary>
public sealed class EnsureService(AgentContext context, IAgentProcessController process,
    Func<TimeSpan, McpEndpointClient>? clientFactory = null)
{
    private readonly Func<TimeSpan, McpEndpointClient> _clients =
        clientFactory ?? (timeout => new McpEndpointClient(context.Endpoint, context.Tokens.Resolve, timeout));

    public async Task<EnsureResult> EnsureAsync(bool allowLaunch, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var settings = context.ReadSettings();
        if (settings.McpEnabled == false)
            return Failure("mcp_disabled", "MCP is disabled in WallBili's preferences.", DisabledStep(), started);

        var appPath = context.ResolveAppPath();
        var launched = false;
        using var client = _clients(TimeSpan.FromSeconds(3));
        while (started.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Tokens.Resolve() is not null)
            {
                try
                {
                    await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    return EnsureResult.Success(launched, context.Endpoint.AbsoluteUri, appPath,
                        started.ElapsedMilliseconds);
                }
                catch (McpException exception) when (exception.Code == "token_invalid")
                {
                    return Failure("token_invalid", "The MCP endpoint rejected the stored access token.",
                        "Copy the MCP configuration again from WallBili's 助手连接 settings, or reset the token there.",
                        started, appPath);
                }
                catch (McpException) { }
            }

            if (!launched)
            {
                if (appPath is null)
                    return Failure("app_not_found", "WallBili.Desktop.exe could not be located.",
                        "Pass --app or set WALLBILI_APP_PATH to WallBili.Desktop.exe.", started);
                // A custom data directory is a separate instance with its own mutex; the launcher
                // cannot tell it apart from the default instance by process name alone, so it lets
                // the application's single-instance logic deduplicate the launch.
                var running = !context.CustomDataDirectory && process.IsRunning(appPath);
                if (!allowLaunch)
                {
                    if (!running)
                        return Failure("app_not_running", "WallBili is not running and launching is disabled.",
                            "Run ensure without --no-launch to start the application.", started, appPath);
                }
                else if (!running)
                {
                    if (!process.Launch(appPath, context.CustomDataDirectory ? context.DataDirectory : null))
                        return Failure("launch_failed", "WallBili could not be started.",
                            "Start WallBili manually and retry, or pass a valid --app path.", started, appPath);
                    launched = true;
                }
            }

            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }

        var finalSettings = context.ReadSettings();
        if (finalSettings.McpEnabled == false)
            return Failure("mcp_disabled", "MCP is disabled in WallBili's preferences.", DisabledStep(), started, appPath);
        if (context.Tokens.Resolve() is null)
            return Failure("token_unavailable", "The application's MCP access token is not readable yet.",
                "Start WallBili once, enable MCP in 助手连接, then retry.", started, appPath);
        if (context.PortWasExplicit && finalSettings.McpPort is { } configured && configured != context.Port)
            return Failure("port_mismatch",
                $"WallBili is configured for port {configured}, but the launcher targeted {context.Port}.",
                "Use the port shown in WallBili's 助手连接 settings, or update the assistant configuration.",
                started, appPath);
        return Failure("timeout",
            $"WallBili did not expose MCP at {context.Endpoint} within {timeout.TotalSeconds:0} seconds.",
            "Check that MCP is enabled in 助手连接 and its port is free, then retry.", started, appPath);

        EnsureResult Failure(string code, string message, string nextStep, Stopwatch watch, string? resolvedApp = null) =>
            EnsureResult.Failure(code, message, nextStep, context.Endpoint.AbsoluteUri, resolvedApp, watch.ElapsedMilliseconds);
    }

    private static string DisabledStep() =>
        "Ask the user to open 助手连接 in WallBili and enable MCP, then retry.";
}
