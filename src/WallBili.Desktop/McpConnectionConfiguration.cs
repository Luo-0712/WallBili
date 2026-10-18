using System.Security.Cryptography;
using System.Text.Json;

namespace WallBili.Desktop;

/// <summary>Connection snippets only contain the independent local MCP credential. Nothing from the
/// Bilibili account or the library is included.</summary>
internal static class McpConnectionConfiguration
{
    public const int DefaultPort = 38291;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;
    public static Uri Endpoint(int port) => IsValidPort(port)
        ? new Uri($"http://127.0.0.1:{port}/mcp")
        : throw new ArgumentOutOfRangeException(nameof(port), "端口须在 1–65535 之间。");

    public static string Http(int port, string token) => JsonSerializer.Serialize(new
    {
        mcpServers = new
        {
            wallbili = new { type = "http", url = Endpoint(port).AbsoluteUri,
                headers = new { Authorization = "Bearer " + token } }
        }
    }, Json);

    public static string Stdio(int port, string token, string bridgePath) => JsonSerializer.Serialize(new
    {
        mcpServers = new
        {
            wallbili = new { command = bridgePath, args = new[] { "--endpoint", Endpoint(port).AbsoluteUri },
                env = new { WALLBILI_MCP_TOKEN = token } }
        }
    }, Json);
}

/// <summary>A separate DPAPI CurrentUser file prevents MCP configuration and Bilibili login from
/// sharing credentials. Resetting access never signs the user out.</summary>
internal sealed class McpTokenStore(string path)
{
    private readonly AuthStore _encrypted = new(path);
    public string GetOrCreate() => _encrypted.Load() is { Length: >= 43 } token ? token : Reset();
    public string Reset()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _encrypted.Save(token);
        return token;
    }
}
