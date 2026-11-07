using WallBili.AgentCli;
using Xunit;

namespace WallBili.AgentCli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void EnsureUsesDefaults()
    {
        var options = CliOptions.Parse(["ensure"]);
        Assert.Equal(CliCommand.Ensure, options.Command);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.False(options.NoLaunch);
    }

    [Fact]
    public void CallAcceptsToolAndInlineArguments()
    {
        var options = CliOptions.Parse(["call", "get_status", """{"itemId":"x"}""", "--no-ensure"]);
        Assert.Equal(CliCommand.Call, options.Command);
        Assert.Equal("get_status", options.ToolName);
        Assert.Equal("""{"itemId":"x"}""", options.ArgumentsJson);
        Assert.True(options.NoEnsure);
    }

    [Fact]
    public void OptionsMayFollowTheCommand()
    {
        var options = CliOptions.Parse(["ensure", "--data-dir", @"C:\data", "--timeout", "12", "--port", "41000"]);
        Assert.Equal(@"C:\data", options.DataDirectory);
        Assert.Equal(12, options.Timeout.TotalSeconds);
        Assert.Equal(41000, options.Port);
    }

    [Theory]
    [InlineData("http://localhost:38291/mcp")]
    [InlineData("https://127.0.0.1:38291/mcp")]
    [InlineData("http://127.0.0.1:38291/other")]
    [InlineData("http://user:pass@127.0.0.1:38291/mcp")]
    public void EndpointMustBeLoopbackHttpMcp(string value) =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["ensure", "--endpoint", value]));

    [Fact]
    public void PortRangeIsValidated() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["ensure", "--port", "70000"]));

    [Fact]
    public void UnknownOptionsAreRejected() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["ensure", "--verbose"]));

    [Fact]
    public void CallRequiresAToolName() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["call"]));
}
