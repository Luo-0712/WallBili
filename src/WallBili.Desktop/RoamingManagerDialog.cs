using System.Globalization;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;

namespace WallBili.Desktop;

internal sealed class RoamingManagerDialog : Window
{
    private readonly LibrarySession _library;
    private readonly RoamingService _roaming;
    private readonly IContentDiscoveryClient _discovery;
    private readonly TextBlock _account, _subscriptions, _status;
    private readonly ListBox _videos;
    private readonly CheckBox _schedule;
    private readonly TextBox _time;
    private bool _working, _closed;

    public RoamingManagerDialog(LibrarySession library, RoamingService roaming, IContentDiscoveryClient discovery)
    {
        _library = library; _roaming = roaming; _discovery = discovery;
        AgentDialogUi.Window(this, "WallBili · 订阅漫游", 860, 680);
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(AgentDialogUi.Text("订阅漫游", "Text.PageTitle"));
        _account = AgentDialogUi.Text("", "Text.Quiet"); _account.Margin = new Thickness(0, 10, 0, 18);
        Grid.SetRow(_account, 1); root.Children.Add(_account);

        var body = new Grid(); Grid.SetRow(body, 2); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(0.9, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1.1, GridUnitType.Star) });
        var left = new DockPanel();
        var leftActions = new WrapPanel();
        leftActions.Children.Add(AgentDialogUi.Button("管理关注 UP 主", Manage_Click, true));
        leftActions.Children.Add(AgentDialogUi.Button("刷新投稿", Refresh_Click));
        DockPanel.SetDock(leftActions, Dock.Top); left.Children.Add(leftActions);
        _subscriptions = AgentDialogUi.Text("还没有选择参与漫游的 UP 主。", "Text.Quiet");
        _subscriptions.TextWrapping = TextWrapping.Wrap; _subscriptions.Margin = new Thickness(0, 16, 0, 0);
        left.Children.Add(_subscriptions); body.Children.Add(left);

        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var rightHeader = new StackPanel();
        rightHeader.Children.Add(AgentDialogUi.Text("已发现投稿", "Text.Section"));
        _status = AgentDialogUi.Text("", "Text.Quiet"); _status.Margin = new Thickness(0, 8, 0, 10);
        rightHeader.Children.Add(_status);
        DockPanel.SetDock(rightHeader, Dock.Top); right.Children.Add(rightHeader);
        _videos = new ListBox { DisplayMemberPath = nameof(VideoRow.Title) };
        AgentDialogUi.Style(_videos, "List.Rows"); right.Children.Add(_videos);

        var bottom = new StackPanel { Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        var scheduleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _schedule = new CheckBox { Content = "每天自动开始漫游", VerticalAlignment = VerticalAlignment.Center };
        scheduleRow.Children.Add(_schedule);
        _time = new TextBox { Text = "09:00", Width = 92, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetName(_time, "自动开始时间");
        AgentDialogUi.Style(_time, "Input.Plain"); scheduleRow.Children.Add(_time);
        scheduleRow.Children.Add(AgentDialogUi.Button("保存计划", SaveSchedule_Click));
        bottom.Children.Add(scheduleRow);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(AgentDialogUi.Button("开始 / 继续漫游", Start_Click, true));
        actions.Children.Add(AgentDialogUi.Button("停止漫游", Stop_Click));
        actions.Children.Add(AgentDialogUi.Button("关闭", (_, _) => Close()));
        bottom.Children.Add(actions);
        Content = root;
        _library.Changed += LibraryChanged;
        _roaming.Changed += RoamingChanged;
        Closed += (_, _) =>
        {
            _closed = true; _library.Changed -= LibraryChanged; _roaming.Changed -= RoamingChanged;
        };
        Refresh();
    }

    private void LibraryChanged(object? sender, EventArgs e) => _ = Dispatcher.BeginInvoke(Refresh);
    private void RoamingChanged(object? sender, EventArgs e) => _ = Dispatcher.BeginInvoke(Refresh);
    private void Refresh()
    {
        if (_closed) return;
        var snapshot = _library.Snapshot.Roaming ?? RoamingSnapshot.Empty;
        _account.Text = snapshot.AccountId is null ? "未登录。登录 B 站并选择关注的 UP 主后即可使用。" : "当前 UID：" + snapshot.AccountId;
        _subscriptions.Text = snapshot.Subscriptions.Count == 0 ? "还没有选择参与漫游的 UP 主。"
            : $"已选择 {snapshot.Subscriptions.Count} 位 UP 主：\n" + string.Join("、", snapshot.Subscriptions.Select(c => c.Name));
        _status.Text = _roaming.IsRefreshing ? "正在刷新投稿；当前播放会继续。" : _roaming.Status;
        _videos.ItemsSource = snapshot.Videos.Select(v => new VideoRow(v.Video.Title + " · " +
            (v.IsNew ? "新投稿" : "已看过") + (v.Failure is { } failure ? " · " + RoamingPresentation.Failure(failure) : ""))).ToArray();
        _schedule.IsChecked = snapshot.ScheduleEnabled;
        if (!_time.IsKeyboardFocused) _time.Text = snapshot.ScheduleTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    private async void Manage_Click(object sender, RoutedEventArgs e)
    {
        var current = _library.Snapshot.Roaming?.Subscriptions ?? [];
        var dialog = new SubscriptionDialog(_discovery, current) { Owner = this };
        if (dialog.ShowDialog() == true) await RunAsync(() => _roaming.SaveSubscriptionsAsync(dialog.SelectedCreators));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _roaming.RefreshAsync());
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _roaming.StartAsync());
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _library.CancelPendingRoamingAsync();
        await _library.StopAsync();
    });
    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeSpan.TryParseExact(_time.Text.Trim(), [@"h:mm", @"hh:mm"], CultureInfo.InvariantCulture, out var time) ||
            time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        { _status.Text = "请输入 00:00 到 23:59 之间的时间。"; return; }
        await RunAsync(() => _roaming.SetScheduleAsync(_schedule.IsChecked == true, time));
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_working || _closed) return;
        _working = true; IsEnabled = false;
        try { await action(); Refresh(); }
        catch (ContentAccessException ex) { if (!_closed) _status.Text = RoamingPresentation.Failure(ex.Kind.ToString()); }
        catch { if (!_closed) _status.Text = "订阅漫游操作未完成，请检查登录状态或网络后重试。"; }
        finally { _working = false; if (!_closed) IsEnabled = true; }
    }

    private sealed record VideoRow(string Title);
}
