using System.IO;
using System.Text.Json;
using System.Windows;
using WallBili.Core;
using WallBili.Mcp;

namespace WallBili.Desktop;

public partial class MainWindow
{
    private readonly SemaphoreSlim _mcpLifecycle = new(1, 1);
    private readonly McpTokenStore _mcpTokens;
    private readonly HashSet<Task> _agentOperations = [];
    private AgentControlService? _agent;
    private McpServerHost? _mcpHost;
    private LocalPlaylistsDialog? _playlistsDialog;
    private string _mcpStatus = "本机 MCP 服务未启用。";

    private async Task InitializeAgentAsync()
    {
        _agent ??= new(_library, _client, _client, SetAgentVideoVisibleAsync);
        try { await RestartMcpAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task RestartMcpAsync(bool resetToken = false)
    {
        await _mcpLifecycle.WaitAsync(_lifetime.Token);
        try
        {
            if (_mcpHost is { } previous)
            {
                _mcpHost = null;
                _mcpStatus = "正在停止本机 MCP 服务…";
                await previous.DisposeAsync();
            }
            if (resetToken)
            {
                _mcpStatus = "正在更新访问令牌…";
                try { _mcpTokens.Reset(); }
                catch
                {
                    _mcpStatus = "令牌更新失败，MCP 服务已停止。请检查本地应用数据目录权限后重新应用。";
                    throw;
                }
            }
            if (_closing || !_preferences.McpEnabled || _agent is null)
            { _mcpStatus = "本机 MCP 服务未启用。"; return; }
            _mcpStatus = "正在启动本机 MCP 服务…";
            try
            {
                _mcpHost = await McpServerHost.StartAsync(_agent, _preferences.McpPort, _mcpTokens.GetOrCreate(), _lifetime.Token);
                _mcpStatus = $"服务运行中 · {_mcpHost.Endpoint}";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            { _mcpStatus = $"服务启动失败 · 端口 {_preferences.McpPort} 可能被占用，请修改端口后重新应用。"; }
            catch
            { _mcpStatus = "服务启动失败，请检查本机服务运行依赖和访问令牌存储权限后重试。"; }
        }
        finally { _mcpLifecycle.Release(); }
    }

    private void OpenMcpSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closing || _agent is null) return;
        var dialog = new McpSettingsDialog(_preferences, () => _mcpStatus, async (enabled, port) =>
        {
            if (_closing) return;
            var updated = _preferences with { McpEnabled = enabled, McpPort = port };
            updated.Save(_preferencesPath);
            _preferences = updated;
            await RestartMcpAsync();
        }, () => RestartMcpAsync(resetToken: true), stdio => stdio
            ? McpConnectionConfiguration.Stdio(_preferences.McpPort, _mcpTokens.GetOrCreate(),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mcp-bridge", "WallBili.McpBridge.exe")))
            : McpConnectionConfiguration.Http(_preferences.McpPort, _mcpTokens.GetOrCreate())) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closing || _agent is null) return;
        if (_playlistsDialog is not null) { _playlistsDialog.Activate(); return; }
        _playlistsDialog = new LocalPlaylistsDialog(() => _library.Snapshot, InvokeAgentFromUiAsync) { Owner = this };
        try { _playlistsDialog.ShowDialog(); }
        finally { _playlistsDialog = null; }
    }

    private async Task<AgentResult> InvokeAgentFromUiAsync(string tool, object arguments)
    {
        if (_closing || _agent is null)
            return AgentResult.Error("application_closing", "应用正在退出。", "请在应用重新运行后重试。");
        var operation = _agent.InvokeAsync(tool, JsonSerializer.SerializeToElement(arguments, AgentJson.Options), _lifetime.Token);
        _agentOperations.Add(operation);
        try { return await operation; }
        finally { _agentOperations.Remove(operation); if (!_closing) Render(); }
    }

    private async Task SetAgentVideoVisibleAsync(bool visible, CancellationToken token)
    {
        if (Dispatcher.CheckAccess()) await ApplyAsync();
        else await Dispatcher.InvokeAsync(ApplyAsync).Task.Unwrap();
        async Task ApplyAsync()
        {
            token.ThrowIfCancellationRequested();
            if (_closing) throw new OperationCanceledException(token);
            var updated = _preferences with { WallpaperEnabled = visible };
            updated.Save(_preferencesPath);
            _preferences = updated;
            RefreshSettings();
            RefreshDesktop();
            if (_outputRebindNeeded)
            {
                _outputRebindNeeded = false;
                await _library.RebindOutputAsync(token);
            }
            if (!_host.Attached && !updated.ContinueAudioWhenHidden) await _library.PauseAsync(token);
        }
    }

    private void RenderAgent(LibrarySnapshot snapshot)
    {
        if (_closing) return;
        _playlistsDialog?.Refresh(snapshot);
        if (snapshot.Agent?.WaitingForContent == true && snapshot.Playback.Intent == PlaybackIntent.Playing)
        {
            if (HeroStateText is not null) HeroStateText.Text = "等待后续内容";
            if (PlaybackIntentText is not null) PlaybackIntentText.Text = "正在准备下一项";
            StatusText.Text = "已就绪内容播放完毕，正在等待后续内容。";
        }
    }

    private async Task StopAgentAsync()
    {
        await _mcpLifecycle.WaitAsync();
        try
        {
            try
            {
                if (_mcpHost is { } host)
                {
                    _mcpHost = null;
                    await host.DisposeAsync();
                }
            }
            finally
            {
                try { await Task.WhenAll(_agentOperations.ToArray()); } catch { }
                if (_agent is { } agent) { _agent = null; await agent.DisposeAsync(); }
            }
        }
        finally { _mcpLifecycle.Release(); }
    }
}
