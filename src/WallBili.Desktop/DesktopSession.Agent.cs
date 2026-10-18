using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using WallBili.Core;
using WallBili.Mcp;

namespace WallBili.Desktop;

internal sealed partial class DesktopSession
{
    private readonly SemaphoreSlim _mcpGate = new(1, 1);
    private readonly CancellationTokenSource _mcpLifetime = new();
    private readonly HashSet<Task> _agentOperations = [];
    private McpTokenStore? _mcpTokens;
    private AgentControlService? _agent;
    private McpServerHost? _mcpHost;
    private string _mcpStatus = "本机 MCP 服务未启用。";

    internal event EventHandler? AgentChanged;
    internal AgentControlService? Agent => _agent;
    internal RoamingService? Roaming => _roaming;
    internal DesktopPreferences Preferences => _preferences;
    internal string McpStatus => _mcpStatus;
    internal int McpPort => _preferences.McpPort;

    private async Task InitializeMcpAsync()
    {
        if (Client is null || _disposed || _mcpLifetime.IsCancellationRequested) return;
        _mcpTokens = new(Path.Combine(_options.DataDirectory, "mcp-token.dat"));
        _agent = new(Library, Client, Client, SetAgentVideoVisibleAsync);
        Library.Changed += LibraryChanged;
        try { await RestartMcpAsync(); }
        catch (OperationCanceledException) when (_mcpLifetime.IsCancellationRequested) { }
    }

    private void LibraryChanged(object? sender, EventArgs e) => AgentChanged?.Invoke(this, EventArgs.Empty);

    private async Task RestartMcpAsync(bool resetToken = false)
    {
        await _mcpGate.WaitAsync(_mcpLifetime.Token);
        try
        {
            await StopMcpHostAsync();
            if (resetToken)
            {
                try { _mcpTokens?.Reset(); }
                catch
                {
                    _mcpStatus = "令牌更新失败，MCP 服务已停止。请检查本地应用数据目录权限后重试。";
                    throw;
                }
            }
            if (_disposed || !_preferences.McpEnabled || _agent is null || _mcpTokens is null)
            {
                _mcpStatus = "本机 MCP 服务未启用。";
                AgentChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            try
            {
                _mcpHost = await McpServerHost.StartAsync(_agent, _preferences.McpPort,
                    _mcpTokens.GetOrCreate(), _mcpLifetime.Token);
                _mcpStatus = $"服务运行中 · {_mcpHost.Endpoint}";
            }
            catch (OperationCanceledException) when (_mcpLifetime.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                _mcpStatus = $"服务启动失败 · 端口 {_preferences.McpPort} 可能被占用，请修改端口后重新应用。";
            }
            catch { _mcpStatus = "服务启动失败，请检查本机运行依赖和令牌存储权限后重试。"; }
            AgentChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _mcpGate.Release(); }
    }

    internal async Task ApplyMcpSettingsAsync(bool enabled, int port)
    {
        if (!McpConnectionConfiguration.IsValidPort(port)) throw new ArgumentOutOfRangeException(nameof(port));
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopSession));
        await _preferencesGate.WaitAsync(_mcpLifetime.Token);
        try
        {
            var updated = _preferences with { McpEnabled = enabled, McpPort = port };
            if (!_options.Preview) updated.Save(Path.Combine(_options.DataDirectory, "preferences.json"));
            _preferences = updated;
            ViewModel.SyncMcpPreferences(enabled, port);
        }
        finally { _preferencesGate.Release(); }
        await RestartMcpAsync();
    }

    internal Task ResetMcpTokenAsync() => RestartMcpAsync(resetToken: true);

    internal string McpConfiguration(bool stdio)
    {
        var port = _preferences.McpPort;
        var token = _mcpTokens?.GetOrCreate() ?? throw new InvalidOperationException("MCP token storage is not ready.");
        if (!stdio) return McpConnectionConfiguration.Http(port, token);
        var bridge = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mcp-bridge", "WallBili.McpBridge.exe"));
        return McpConnectionConfiguration.Stdio(port, token, bridge);
    }

    internal async Task<AgentResult> InvokeAgentFromUiAsync(string tool, object arguments)
    {
        if (_disposed || _agent is null)
            return AgentResult.Error("NotReady", "WallBili is still initializing or closing.", "Retry while the application is running.");
        var task = _agent.InvokeAsync(tool, JsonSerializer.SerializeToElement(arguments, AgentJson.Options), _mcpLifetime.Token);
        _agentOperations.Add(task);
        try { return await task; }
        finally { _agentOperations.Remove(task); }
    }

    private async Task SetAgentVideoVisibleAsync(bool visible, CancellationToken token)
    {
        async Task ApplyAsync()
        {
            token.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException(token);
            await _preferencesGate.WaitAsync(token);
            try
            {
                var preferences = _preferences with { WallpaperEnabled = visible };
                if (!_options.Preview) preferences.Save(Path.Combine(_options.DataDirectory, "preferences.json"));
                _preferences = preferences;
                ViewModel.SetAgentWallpaperEnabled(visible);
                await Library.SetVideoFitAsync(preferences.FillWindow, token);
                var recreated = RefreshDesktop();
                if (recreated) await Library.RebindOutputAsync(token);
                if (!preferences.WallpaperEnabled && !preferences.ContinueAudioWhenHidden && Library.Snapshot.Playback.Intent == PlaybackIntent.Playing)
                    await Library.PauseAsync(token);
            }
            finally { _preferencesGate.Release(); }
        }
        if (_timer.Dispatcher.CheckAccess()) await ApplyAsync();
        else await _timer.Dispatcher.InvokeAsync(ApplyAsync).Task.Unwrap();
    }

    private async Task StopMcpHostAsync()
    {
        if (_mcpHost is not { } host) return;
        _mcpHost = null;
        _mcpStatus = "正在停止本机 MCP 服务…";
        await host.DisposeAsync();
    }

    private async Task StopAgentAsync()
    {
        _mcpLifetime.Cancel();
        await _mcpGate.WaitAsync();
        try
        {
            try { await StopMcpHostAsync(); }
            finally
            {
                try { await Task.WhenAll(_agentOperations.ToArray()); } catch { }
                if (_agent is { } agent) { _agent = null; await agent.DisposeAsync(); }
                Library.Changed -= LibraryChanged;
            }
        }
        finally { _mcpGate.Release(); _mcpGate.Dispose(); _mcpLifetime.Dispose(); }
    }
}
