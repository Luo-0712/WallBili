using WallBili.AgentCli;
using Xunit;

namespace WallBili.AgentCli.Tests;

internal sealed class FakeProcessController : IAgentProcessController
{
    public int Launches;
    public bool Running;
    public Func<Task>? OnLaunch { get; set; }

    public bool IsRunning(string appPath) => Running;

    public bool Launch(string appPath, string? dataDirectory)
    {
        Launches++;
        Running = true;
        if (OnLaunch is not null) _ = Task.Run(OnLaunch);
        return true;
    }
}

public sealed class EnsureServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadyEndpointNeedsNoLaunch()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        await environment.StartServerAsync(port);
        environment.WritePreferences(true, port);
        var context = AgentContext.Create(environment.Options(port));
        var process = new FakeProcessController();

        var result = await new EnsureService(context, process)
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(5), Cancellation);

        Assert.True(result.Ok);
        Assert.False(result.Launched);
        Assert.Equal(0, process.Launches);
        Assert.Equal($"http://127.0.0.1:{port}/mcp", result.Endpoint);
    }

    [Fact]
    public async Task LaunchesOnceAndWaitsForTheEndpoint()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        environment.WritePreferences(true, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port));
        var process = new FakeProcessController
        {
            OnLaunch = async () => { await Task.Delay(400); await environment.StartServerAsync(port); }
        };

        var result = await new EnsureService(context, process)
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(10), Cancellation);

        Assert.True(result.Ok);
        Assert.True(result.Launched);
        Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task DisabledPreferenceIsReportedWithoutLaunching()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        environment.WritePreferences(false, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port));
        var process = new FakeProcessController();

        var result = await new EnsureService(context, process)
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(2), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("mcp_disabled", result.Code);
        Assert.Equal(0, process.Launches);
    }

    [Fact]
    public async Task RejectedTokenStopsPolling()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        await environment.StartServerAsync(port);
        environment.WritePreferences(true, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port, token: "wrong-token-0123456789012345678901234567"));

        var result = await new EnsureService(context, new FakeProcessController())
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(5), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("token_invalid", result.Code);
    }

    [Fact]
    public async Task CustomProfileAttemptsALaunchAndTimesOutWithoutAnEndpoint()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        environment.WritePreferences(true, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port, timeout: 1));
        var process = new FakeProcessController { Running = true };

        var result = await new EnsureService(context, process)
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(1), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Code);
        // A custom data directory is a distinct instance; process-name detection cannot tell it from
        // the default profile, so one launch is delegated to the application's own mutex.
        Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task MissingTokenIsReportedAfterTheWait()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        await environment.StartServerAsync(port);
        environment.WritePreferences(true, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port, token: null));

        var result = await new EnsureService(context, new FakeProcessController())
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(1), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("token_unavailable", result.Code);
    }

    [Fact]
    public async Task MissingApplicationIsReported()
    {
        await using var environment = new TestEnvironment();
        var missing = Path.Combine(environment.Directory, "missing.exe");
        var context = AgentContext.Create(environment.Options(TestEnvironment.AvailablePort(), app: missing));

        var result = await new EnsureService(context, new FakeProcessController())
            .EnsureAsync(allowLaunch: true, TimeSpan.FromSeconds(1), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("app_not_found", result.Code);
    }

    [Fact]
    public async Task NoLaunchReturnsImmediatelyWhenNotRunning()
    {
        await using var environment = new TestEnvironment();
        var port = TestEnvironment.AvailablePort();
        environment.WritePreferences(true, port);
        environment.WriteManifest(environment.AppPath);
        var context = AgentContext.Create(environment.Options(port, noLaunch: true));
        var process = new FakeProcessController();

        var result = await new EnsureService(context, process)
            .EnsureAsync(allowLaunch: false, TimeSpan.FromSeconds(30), Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("app_not_running", result.Code);
        Assert.Equal(0, process.Launches);
        Assert.True(result.ElapsedMs < 3000);
    }
}
