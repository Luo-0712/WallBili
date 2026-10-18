using System.Windows;
using WallBili.Core;

namespace WallBili.Desktop;

public partial class MainWindow
{
    private LocalPlaylistsDialog? _playlistsDialog;
    private RoamingManagerDialog? _roamingDialog;

    private void OpenMcpSettings()
    {
        if (_session is null || App.Options.Preview)
        { _viewModel.Report("预览不会启动本机 MCP 服务。正常启动后可在此配置助手连接。"); return; }
        var dialog = new McpSettingsDialog(_session.Preferences, () => _session.McpStatus,
            _session.ApplyMcpSettingsAsync, _session.ResetMcpTokenAsync,
            stdio => _session.McpConfiguration(stdio)) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenPlaylists()
    {
        if (_session?.Agent is null)
        { _viewModel.Report("片单服务仍在初始化，请稍后重试。"); return; }
        if (_playlistsDialog is not null) { _playlistsDialog.Activate(); return; }
        _playlistsDialog = new LocalPlaylistsDialog(() => _session.Library.Snapshot, _session.InvokeAgentFromUiAsync) { Owner = this };
        try { _playlistsDialog.ShowDialog(); }
        finally { _playlistsDialog = null; }
    }

    private void OpenRoaming()
    {
        if (_session?.Roaming is null || _session.Client is null)
        { _viewModel.Report("预览中无法访问关注列表。正常启动并登录后可管理订阅漫游。"); return; }
        if (_roamingDialog is not null) { _roamingDialog.Activate(); return; }
        _roamingDialog = new RoamingManagerDialog(_session.Library, _session.Roaming, _session.Client) { Owner = this };
        try { _roamingDialog.ShowDialog(); }
        finally { _roamingDialog = null; }
    }

    private void SessionAgentChanged(object? sender, EventArgs e)
    {
        if (_closing || _session is null) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closing) _playlistsDialog?.Refresh(_session.Library.Snapshot);
        });
    }
}
