using System.Security.Cryptography;
using System.Text;

namespace WallBili.AgentCli;

/// <summary>Resolves the MCP access token in the same order the bridge does, falling back to the
/// application's DPAPI CurrentUser file. The value only lives in memory and is never printed.</summary>
public sealed class TokenProvider(string? explicitToken, string dataDirectory)
{
    public const string EnvironmentVariable = "WALLBILI_MCP_TOKEN";

    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
        return ReadProtectedFile(Path.Combine(dataDirectory, "mcp-token.dat"));
    }

    private static string? ReadProtectedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return null;
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }
}
