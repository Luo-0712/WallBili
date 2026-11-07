using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallBili.AgentCli;
using Xunit;

namespace WallBili.AgentCli.Tests;

public sealed class AgentContextTests
{
    [Fact]
    public async Task PreferencePortIsUsedWhenNothingIsSpecified()
    {
        await using var environment = new TestEnvironment();
        environment.WritePreferences(true, 41000);
        var context = AgentContext.Create(new CliOptions { Command = CliCommand.Ensure, DataDirectory = environment.Directory });
        Assert.Equal(41000, context.Port);
        Assert.Equal("http://127.0.0.1:41000/mcp", context.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task ExplicitPortOverridesPreferences()
    {
        await using var environment = new TestEnvironment();
        environment.WritePreferences(true, 41000);
        var context = AgentContext.Create(new CliOptions
        {
            Command = CliCommand.Ensure, DataDirectory = environment.Directory, Port = 42000
        });
        Assert.Equal(42000, context.Port);
    }

    [Fact]
    public async Task ExplicitEndpointOverridesEverything()
    {
        await using var environment = new TestEnvironment();
        environment.WritePreferences(true, 41000);
        var context = AgentContext.Create(new CliOptions
        {
            Command = CliCommand.Ensure,
            DataDirectory = environment.Directory,
            Port = 42000,
            Endpoint = new Uri("http://127.0.0.1:43000/mcp")
        });
        Assert.Equal(43000, context.Port);
    }

    [Fact]
    public async Task ManifestAppPathIsUsedWhenNoOptionIsGiven()
    {
        await using var environment = new TestEnvironment();
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(new CliOptions { Command = CliCommand.Ensure, DataDirectory = environment.Directory });
        Assert.Equal(Path.GetFullPath(environment.AppPath), context.ResolveAppPath());
    }

    [Fact]
    public async Task MalformedPreferencesFallBackToTheDefaultPort()
    {
        await using var environment = new TestEnvironment();
        File.WriteAllText(environment.PreferencesPath, "{not json");
        var context = AgentContext.Create(new CliOptions { Command = CliCommand.Ensure, DataDirectory = environment.Directory });
        Assert.Equal(38291, context.Port);
    }
}

public sealed class TokenProviderTests
{
    [Fact]
    public async Task ExplicitTokenWins()
    {
        await using var environment = new TestEnvironment();
        var provider = new TokenProvider("explicit-token", environment.Directory);
        Assert.Equal("explicit-token", provider.Resolve());
    }

    [Fact]
    public async Task EnvironmentTokenWinsOverTheEncryptedFile()
    {
        await using var environment = new TestEnvironment();
        WriteProtected(Path.Combine(environment.Directory, "mcp-token.dat"), "file-token");
        Environment.SetEnvironmentVariable(TokenProvider.EnvironmentVariable, "environment-token");
        try
        {
            var provider = new TokenProvider(null, environment.Directory);
            Assert.Equal("environment-token", provider.Resolve());
        }
        finally { Environment.SetEnvironmentVariable(TokenProvider.EnvironmentVariable, null); }
    }

    [Fact]
    public async Task EncryptedFileIsReadInTheSameUserScope()
    {
        await using var environment = new TestEnvironment();
        WriteProtected(Path.Combine(environment.Directory, "mcp-token.dat"), "file-token");
        var provider = new TokenProvider(null, environment.Directory);
        Assert.Equal("file-token", provider.Resolve());
    }

    [Fact]
    public async Task CorruptFileIsTreatedAsMissing()
    {
        await using var environment = new TestEnvironment();
        File.WriteAllBytes(Path.Combine(environment.Directory, "mcp-token.dat"), [1, 2, 3, 4]);
        var provider = new TokenProvider(null, environment.Directory);
        Assert.Null(provider.Resolve());
    }

    private static void WriteProtected(string path, string value) =>
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null,
            DataProtectionScope.CurrentUser));
}

public sealed class AgentFileParsingTests
{
    [Fact]
    public async Task SettingsIgnoreUnknownFields()
    {
        await using var environment = new TestEnvironment();
        File.WriteAllText(environment.PreferencesPath,
            """{"MpvPath":"mpv","McpEnabled":true,"McpPort":41111,"Extra":"ignored"}""");
        var settings = AgentSettings.Read(environment.Directory);
        Assert.True(settings.McpEnabled);
        Assert.Equal(41111, settings.McpPort);
    }

    [Fact]
    public async Task ManifestReadsCamelCaseFields()
    {
        await using var environment = new TestEnvironment();
        File.WriteAllText(Path.Combine(environment.Directory, "agent.json"),
            JsonSerializer.Serialize(new { appPath = @"C:\Apps\WallBili\app\WallBili.Desktop.exe", cliPath = @"C:\Apps\WallBili\agent-cli\WallBili.AgentCli.exe" }));
        var manifest = AgentManifest.Read(environment.Directory);
        Assert.Equal(@"C:\Apps\WallBili\app\WallBili.Desktop.exe", manifest.AppPath);
        Assert.Equal(@"C:\Apps\WallBili\agent-cli\WallBili.AgentCli.exe", manifest.CliPath);
    }
}
