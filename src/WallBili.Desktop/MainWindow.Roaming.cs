using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Player.Mpv;

namespace WallBili.Desktop;

public partial class MainWindow
{
    private readonly RoamingService _roaming;
    private readonly HashSet<Task> _roamingOperations = [];
    private Task _roamingTick = Task.CompletedTask;
    private Task _roamingAccountTask = Task.CompletedTask;
    private bool _hasBoundRoamingAccount, _startingRoaming, _savingRoamingSchedule;
    private long? _boundRoamingAccount;
    private (long? Account, bool Enabled, TimeSpan Time)? _renderedRoamingSchedule;
    private string? _roamingError;

    private Task SynchronizeRoamingAccountAsync(CancellationToken token)
    {
        var account = _client.CurrentAccount()?.Uid;
        if (_hasBoundRoamingAccount && account == _boundRoamingAccount &&
            !_roamingAccountTask.IsFaulted && !_roamingAccountTask.IsCanceled) return _roamingAccountTask;
        var previous = _roamingAccountTask;
        _hasBoundRoamingAccount = true;
        _boundRoamingAccount = account;
        _roamingAccountTask = SynchronizeCoreAsync();
        return _roamingAccountTask;

        async Task SynchronizeCoreAsync()
        {
            try { await previous; } catch { /* A new account still gets an independent attempt. */ }
            token.ThrowIfCancellationRequested();
            if (account != _boundRoamingAccount) return;
            await _roaming.SetAccountAsync(account, token);
            _roamingError = null;
            RenderRoaming();
        }
    }

    private void AccountChanged(object? sender, EventArgs e) => _ = Dispatcher.InvokeAsync(async () =>
    {
        if (_closing) return;
        RenderAccount();
        if (_ready) await RunRoamingAsync(SynchronizeRoamingAccountAsync);
    });

    private void QueueRoamingTick()
    {
        if (_closing || !_ready || _suspended || _powerSuspendRequested || !_roamingTick.IsCompleted) return;
        _roamingTick = RunRoamingAsync(async ct =>
        {
            await SynchronizeRoamingAccountAsync(ct);
            await _roaming.TickAsync(ct);
        }, clearError: false);
    }

    /// <summary>Discovery and scheduled checks never take the main-window busy gate. The shared
    /// player's pause/stop buttons remain usable while a slow remote request is pending.</summary>
    private async Task RunRoamingAsync(Func<CancellationToken, Task> operation, bool clearError = true)
    {
        if (_closing || !_ready) return;
        if (clearError) _roamingError = null;
        Task? pending = null;
        try
        {
            pending = operation(_lifetime.Token);
            _roamingOperations.Add(pending);
            RenderRoaming();
            await pending;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _roamingError = ex switch
            {
                ContentAccessException content => "订阅访问失败：" + RoamingPresentation.Failure(content.Kind.ToString()) + "。已记录的内容仍会保留。",
                MpvException player => $"播放器错误（{player.Code}），请检查 mpv 路径后重试。",
                SqliteException or IOException or UnauthorizedAccessException => "订阅资料读写失败，请检查本地应用数据目录。",
                OperationCanceledException or HttpRequestException => "订阅请求失败或超时，可稍后重试；已记录的内容仍会保留。",
                ArgumentException or FormatException => "自动开播时间无效，请使用 HH:mm 格式，例如 09:00。",
                _ => "订阅操作未完成，请检查登录、网络及播放器后重试。"
            };
        }
        finally
        {
            if (pending is not null) _roamingOperations.Remove(pending);
            if (!_closing) { RenderRoaming(); Render(); }
        }
    }

    private async void ManageSubscriptions_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closing || _client.CurrentAccount() is not { } account) return;
        var dialog = new SubscriptionDialog(_client, (_library.Snapshot.Roaming ?? RoamingSnapshot.Empty).Subscriptions) { Owner = this };
        if (dialog.ShowDialog() != true || _client.CurrentAccount()?.Uid != account.Uid) return;
        await RunRoamingAsync(ct => _roaming.SaveSubscriptionsAsync(dialog.SelectedCreators, ct));
        // Seed the chosen sources without making the user find or import any URLs.
        await RunRoamingAsync(ct => _roaming.RefreshAsync(ct));
    }

    private async void RefreshRoaming_Click(object sender, RoutedEventArgs e) =>
        await RunRoamingAsync(ct => _roaming.RefreshAsync(ct));

    private async void StartRoaming_Click(object sender, RoutedEventArgs e)
    {
        if (_startingRoaming || !_ready || _closing) return;
        _startingRoaming = true;
        Volatile.Write(ref _mpvPath, string.IsNullOrWhiteSpace(MpvPathInput.Text) ? "mpv" : MpvPathInput.Text.Trim());
        try
        {
            await RunRoamingAsync(async ct =>
            {
                if (_preferences.MpvPath != _mpvPath)
                {
                    _preferences = _preferences with { MpvPath = _mpvPath };
                    _preferences.Save(_preferencesPath);
                }
                await _roaming.StartAsync(ct);
            });
        }
        finally { _startingRoaming = false; if (!_closing) RenderRoaming(); }
    }

    private async void CancelRoaming_Click(object sender, RoutedEventArgs e) =>
        await RunRoamingAsync(ct => _library.CancelPendingRoamingAsync(ct));

    private async void SaveRoamingSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_savingRoamingSchedule) return;
        if (!TimeSpan.TryParseExact(RoamingScheduleInput.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
        {
            _roamingError = "自动开播时间无效，请使用 HH:mm 格式，例如 09:00。";
            RenderRoaming();
            return;
        }
        _savingRoamingSchedule = true;
        try { await RunRoamingAsync(ct => _roaming.SetScheduleAsync(RoamingScheduleBox.IsChecked == true, time, ct)); }
        finally { _savingRoamingSchedule = false; if (!_closing) RenderRoaming(); }
    }

    private void RenderRoaming()
    {
        if (_closing || RoamingPanel is null) return;
        var snapshot = _library.Snapshot;
        var state = snapshot.Roaming ?? RoamingSnapshot.Empty;
        var loggedIn = state.AccountId is not null;
        RoamingPanel.IsEnabled = _ready;
        RoamingManageButton.IsEnabled = loggedIn;
        RoamingRefreshButton.IsEnabled = loggedIn && state.Subscriptions.Count > 0 && !_roaming.IsRefreshing;
        RoamingStartButton.IsEnabled = loggedIn && state.Subscriptions.Count > 0 && !_startingRoaming;
        var active = snapshot.Source == PlaybackSource.Roaming;
        RoamingPauseButton.IsEnabled = RoamingStopButton.IsEnabled =
            (active || _startingRoaming || state.PendingStart) && !_busy;
        RoamingNextButton.IsEnabled = active && !_busy;
        RoamingScheduleSaveButton.IsEnabled = loggedIn && !_savingRoamingSchedule;
        RoamingScheduleBox.IsEnabled = RoamingScheduleInput.IsEnabled = loggedIn;
        var schedule = (state.AccountId, state.ScheduleEnabled, state.ScheduleTime);
        if (_renderedRoamingSchedule != schedule)
        {
            _renderedRoamingSchedule = schedule;
            RoamingScheduleBox.IsChecked = state.ScheduleEnabled;
            RoamingScheduleInput.Text = state.ScheduleTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
        var next = RoamingPresentation.NextScheduledAt(state, DateTime.Now);
        RoamingNextScheduleText.Text = next is { } at ? $"下次计划：{at:MM-dd HH:mm}" : "未开启自动开播";
        RoamingPendingPanel.Visibility = state.PendingStart ? Visibility.Visible : Visibility.Collapsed;
        var completed = state.Videos.Count(v => v.CompletedAt is not null);
        var updated = state.Sync.Values.Where(s => s.LastSuccessfulAt is not null).Select(s => s.LastSuccessfulAt!.Value).ToArray();
        RoamingSummaryText.Text = loggedIn
            ? $"{state.Subscriptions.Count} 位 UP 主 · {state.Videos.Count} 个视频 · {state.Videos.Count - completed} 个未播" +
              (updated.Length > 0 ? $" · 上次更新 {updated.Max().ToLocalTime():MM-dd HH:mm}" : " · 尚未完成更新")
            : "登录后，选择喜欢的 UP 主即可直接播放";
        var selectedBvid = (RoamingGrid.SelectedItem as RoamingRow)?.Bvid;
        RoamingGrid.ItemsSource = state.Videos.OrderByDescending(v => v.Video.PublishedAt).Select(v => new RoamingRow(
            v.Video.Bvid, v.Video.Title, v.Video.Uploader, v.Video.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            v.Failure is not null ? RoamingPresentation.Failure(v.Failure)
                : v.CompletedAt is not null ? "已播" : v.IsNew ? "待播新作" : "待播旧作")).ToArray();
        RoamingGrid.SelectedItem = RoamingGrid.Items.Cast<RoamingRow>().FirstOrDefault(v => v.Bvid == selectedBvid);
        var failures = state.Sync.Values.Where(s => s.Failure is not null).Select(s => RoamingPresentation.Failure(s.Failure)).Distinct().ToArray();
        RoamingStatusText.Text = _roamingError ?? (_roaming.IsRefreshing ? "正在检查订阅更新，当前播放和暂停/停止仍可使用…" : _roaming.Status);
        if (failures.Length > 0) RoamingStatusText.Text += " · 部分来源失败：" + string.Join("；", failures) +
            (state.Videos.Count > 0 ? "；可继续使用已记录内容。" : "。");
        PlaybackSourceText.Text = active ? "订阅漫游" : "手动播放";
        QueueUpButton.IsEnabled = QueueDownButton.IsEnabled = RemoveQueueButton.IsEnabled = !active && !_busy;
        ModeBox.IsEnabled = !active;
    }

    private sealed record RoamingRow(string Bvid, string Title, string Uploader, string Published, string Status);
}
