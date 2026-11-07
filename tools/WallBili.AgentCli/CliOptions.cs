namespace WallBili.AgentCli;

public enum CliCommand
{
    Ensure,
    Call,
    List
}

/// <summary>Command line for the assistant-facing launcher. Tokens are never echoed back.</summary>
public sealed record CliOptions
{
    public required CliCommand Command { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? DataDirectory { get; init; }
    public string? AppPath { get; init; }
    public int? Port { get; init; }
    public Uri? Endpoint { get; init; }
    public string? Token { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool NoLaunch { get; init; }
    public bool NoEnsure { get; init; }
    public bool EnsureTools { get; init; }
    public bool Help { get; init; }

    public const string Usage =
        """
        WallBili.AgentCli <command> [options]

        Commands:
          ensure [options]                 Start WallBili in the background if needed, then wait for MCP.
          call <tool> ['<json>'|@file|-]   Ensure readiness, then invoke one MCP tool.
          list [--ensure]                  List the tools exposed by the running application.

        Options:
          --data-dir PATH   WallBili data directory (default: %LOCALAPPDATA%\WallBili)
          --app PATH        WallBili.Desktop.exe to launch
          --port N          MCP port (default: preferences, then 38291)
          --endpoint URL    MCP endpoint (http://127.0.0.1:PORT/mcp)
          --token TOKEN     Access token (default: WALLBILI_MCP_TOKEN, then the app's encrypted token)
          --timeout SEC     Readiness timeout for ensure (default 30)
          --no-launch       Never start the application
          --no-ensure       For call: invoke directly without ensuring readiness

        Output is one JSON object on stdout. Exit codes: 0 success, 1 tool error, 2 usage, 3 unavailable.
        """;

    public static CliOptions Parse(string[] args)
    {
        CliCommand? command = null;
        string? tool = null;
        string? arguments = null;
        string? dataDirectory = null;
        string? appPath = null;
        int? port = null;
        Uri? endpoint = null;
        string? token = null;
        var timeout = TimeSpan.FromSeconds(30);
        var noLaunch = false;
        var noEnsure = false;
        var ensureTools = false;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help" or "-h":
                    help = true;
                    continue;
                case "--data-dir":
                    dataDirectory = Value(args, ref index, arg);
                    continue;
                case "--app":
                    appPath = Value(args, ref index, arg);
                    continue;
                case "--port":
                    if (!int.TryParse(Value(args, ref index, arg), out var parsedPort) || parsedPort is < 1 or > 65535)
                        throw new ArgumentException("--port must be a number between 1 and 65535.");
                    port = parsedPort;
                    continue;
                case "--endpoint":
                    endpoint = ParseEndpoint(Value(args, ref index, arg));
                    continue;
                case "--token":
                    token = Value(args, ref index, arg);
                    continue;
                case "--timeout":
                    if (!double.TryParse(Value(args, ref index, arg), out var seconds) || seconds is <= 0 or > 600)
                        throw new ArgumentException("--timeout must be a number of seconds between 1 and 600.");
                    timeout = TimeSpan.FromSeconds(seconds);
                    continue;
                case "--no-launch":
                    noLaunch = true;
                    continue;
                case "--no-ensure":
                    noEnsure = true;
                    continue;
                case "--ensure":
                    ensureTools = true;
                    continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown option '{arg}'.");

            if (command is null)
            {
                command = arg switch
                {
                    "ensure" => CliCommand.Ensure,
                    "call" => CliCommand.Call,
                    "list" => CliCommand.List,
                    _ => throw new ArgumentException($"Unknown command '{arg}'.")
                };
                continue;
            }

            if (command == CliCommand.Call && tool is null)
            {
                tool = arg;
                continue;
            }

            if (command == CliCommand.Call && arguments is null)
            {
                arguments = arg;
                continue;
            }

            throw new ArgumentException($"Unexpected argument '{arg}'.");
        }

        if (help) return new() { Command = command ?? CliCommand.Ensure, Help = true };
        if (command is null) throw new ArgumentException("A command is required.");
        if (command == CliCommand.Call && string.IsNullOrWhiteSpace(tool))
            throw new ArgumentException("call requires a tool name.");
        return new()
        {
            Command = command.Value,
            ToolName = tool,
            ArgumentsJson = arguments,
            DataDirectory = dataDirectory,
            AppPath = appPath,
            Port = port,
            Endpoint = endpoint,
            Token = token,
            Timeout = timeout,
            NoLaunch = noLaunch,
            NoEnsure = noEnsure,
            EnsureTools = ensureTools
        };
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"{option} requires a value.");
        return args[++index];
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            endpoint.Host != "127.0.0.1" || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/mcp" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("--endpoint must be http://127.0.0.1:PORT/mcp without credentials, query, or fragment.");
        return endpoint;
    }
}
