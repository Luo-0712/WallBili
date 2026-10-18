using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WallBili.Desktop;

internal sealed class McpSettingsDialog : Window
{
    private readonly CheckBox _enabled;
    private readonly TextBox _port;
    private readonly TextBlock _status, _notice;
    private readonly StackPanel _actions;
    private readonly Func<string> _readStatus;
    private readonly Func<bool, int, Task> _apply;
    private readonly Func<Task> _reset;
    private readonly Func<bool, string> _configuration;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _working, _closed;

    public McpSettingsDialog(DesktopPreferences preferences, Func<string> readStatus,
        Func<bool, int, Task> apply, Func<Task> reset, Func<bool, string> configuration)
    {
        _readStatus = readStatus; _apply = apply; _reset = reset; _configuration = configuration;
        AgentDialogUi.Window(this, "WallBili · 助手连接", 690, 605);
        var root = new StackPanel { Margin = new Thickness(30) };
        root.Children.Add(AgentDialogUi.Text("助手连接", "Text.PageTitle"));
        var introduction = AgentDialogUi.Text("通过本机 MCP，让你的助手搜索内容、整理片单并控制播放。应用收起到托盘后仍可连接。", "Text.Body");
        introduction.Margin = new Thickness(0, 14, 0, 24); root.Children.Add(introduction);
        _enabled = new CheckBox { Name = "McpEnabledBox", Content = "启用本机 MCP 服务", IsChecked = preferences.McpEnabled };
        AgentDialogUi.Style(_enabled, "Check.Switch"); root.Children.Add(_enabled);
        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
        var portLabel = AgentDialogUi.Text("监听端口", "Text.Label"); portLabel.VerticalAlignment = VerticalAlignment.Center;
        portRow.Children.Add(portLabel);
        _port = new TextBox { Name = "McpPortInput", Text = preferences.McpPort.ToString(CultureInfo.InvariantCulture), Width = 120, Margin = new Thickness(20, 0, 0, 0) };
        AgentDialogUi.Style(_port, "Input.Plain"); portRow.Children.Add(_port); root.Children.Add(portRow);
        _status = AgentDialogUi.Text(readStatus()); _status.Name = "McpConnectionStatus";
        _status.Margin = new Thickness(0, 20, 0, 0); root.Children.Add(_status);
        var boundary = AgentDialogUi.Text("地址仅限 127.0.0.1，使用独立访问令牌。修改端口或开关后点击应用；端口被占用时可修改后重试。", "Text.Quiet");
        boundary.Margin = new Thickness(0, 14, 0, 24); root.Children.Add(boundary);
        _actions = new StackPanel { Orientation = Orientation.Horizontal };
        _actions.Children.Add(AgentDialogUi.Button("应用连接设置", Apply_Click, true));
        _actions.Children.Add(AgentDialogUi.Button("重置访问令牌", Reset_Click)); root.Children.Add(_actions);
        var copyActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 28, 0, 0) };
        copyActions.Children.Add(AgentDialogUi.Button("复制 HTTP 配置", (_, _) => Copy(false)));
        copyActions.Children.Add(AgentDialogUi.Button("复制 stdio 配置", (_, _) => Copy(true))); root.Children.Add(copyActions);
        var help = AgentDialogUi.Text("复制的是已应用设置的通用 mcpServers 配置，包含访问令牌，请粘贴到你信任的助手中。stdio 适用于仅支持启动本地命令的客户端。重置令牌后需重新复制配置。", "Text.Quiet");
        help.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(help);
        _notice = AgentDialogUi.Text(""); _notice.Margin = new Thickness(0, 18, 0, 0); root.Children.Add(_notice);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _timer.Tick += (_, _) => _status.Text = _readStatus();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !McpConnectionConfiguration.IsValidPort(port))
        { _notice.Text = "请输入 1–65535 之间的端口。"; return; }
        await ChangeAsync(() => _apply(_enabled.IsChecked == true, port));
    }
    private async void Reset_Click(object sender, RoutedEventArgs e) => await ChangeAsync(_reset);
    private async Task ChangeAsync(Func<Task> action)
    {
        if (_working || _closed) return;
        _working = true; _actions.IsEnabled = false; _notice.Text = "正在更新连接…";
        try
        {
            await action();
            if (!_closed) { _status.Text = _readStatus(); _notice.Text = "设置已保存。连接失败时可调整端口后重新应用。"; }
        }
        catch (OperationCanceledException) { if (!_closed) _notice.Text = "应用正在退出。"; }
        catch { if (!_closed) _notice.Text = "连接设置未能保存，请检查本地应用数据目录权限后重试。"; }
        finally { _working = false; if (!_closed) _actions.IsEnabled = true; }
    }
    private void Copy(bool stdio)
    {
        try
        {
            Clipboard.SetText(_configuration(stdio));
            _notice.Text = "已复制已应用设置的配置。请先启用服务，再在助手中连接。";
        }
        catch (ExternalException) { _notice.Text = "剪贴板正在被其他应用使用，请稍后重试。"; }
        catch { _notice.Text = "无法生成连接配置，请检查访问令牌存储权限。"; }
    }
}
