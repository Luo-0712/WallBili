using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using WallBili.Infrastructure.Storage;
using WallBili.Player.Mpv;

namespace WallBili.Desktop;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new(new HttpClientHandler
        { AutomaticDecompression = DecompressionMethods.All, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly BilibiliClient _client;
    private readonly AuthStore _authStore;
    private readonly LibrarySession _library;
    private readonly MpvQueuePlayer _player;
    private readonly DesktopVideoHost _host;
    private readonly TrayIcon _tray;
    private DesktopPreferences _preferences;
    private long _outputHandle;
    private bool _exitRequested, _suspended, _powerSuspendRequested, _powerResumeRequested, _networkChanged;
    private bool _outputRebindNeeded;
    private Task _pending = Task.CompletedTask;
    private bool _busy, _closing, _closed, _rendering, _ready, _loaded;
    private bool _accountRestorePending;
    private DateTimeOffset _accountRetryAt;
    private string _mpvPath;
    private readonly string _preferencesPath;

    public MainWindow()
    {
        InitializeComponent();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallBili");
        _preferencesPath = Path.Combine(directory, "preferences.json");
        _preferences = DesktopPreferences.Read(_preferencesPath);
        _mpvPath = string.IsNullOrWhiteSpace(_preferences.MpvPath) ? "mpv" : _preferences.MpvPath;
        MpvPathInput.Text = _mpvPath;
        _host = new();
        _outputHandle = _host.Handle.ToInt64();
        _player = new(() => new() { ExecutablePath = Volatile.Read(ref _mpvPath), WindowId = Volatile.Read(ref _outputHandle),
            FillWindow = _preferences.FillWindow });
        _client = new BilibiliClient(_http);
        _client.AccountChanged += AccountChanged;
        _authStore = new(Path.Combine(directory, "auth.dat"));
        _library = new(_client, new SqliteLibraryStore(Path.Combine(directory, "library.db")), _player,
            selection: ToSelection(_preferences));
        _library.Changed += (_, _) => _ = Dispatcher.InvokeAsync(Render);
        _roaming = new(_library, _client);
        _roaming.Changed += (_, _) => _ = Dispatcher.InvokeAsync(RenderRoaming);
        _tray = new(Dispatcher, ShowControls, TogglePause, () => Next_Click(this, new()), ToggleWallpaper, RequestExit);
        RefreshSettings();
        RefreshDesktop();
        RenderAccount();
        // 顶栏筛选：TextChanged 不能写在 XAML 里（XAML 需要能脱离处理器单独解析），所以在这里挂。
        SearchBox.TextChanged += (_, _) => { if (_ready) Render(); };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        _timer.Tick += async (_, _) =>
        {
            if (!_closing && _ready) QueueRoamingTick();
            if (!_busy && !_closing && _ready) await MaintainAsync();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (Environment.GetCommandLineArgs().Contains("--background")) Hide();
        await RunAsync(async ct =>
        {
            await RestoreAccountAsync(ct); // The stored session is restored before playback resumes.
            await _library.InitializeAsync(ct);
            await SynchronizeRoamingAccountAsync(ct);
            if (!_host.Attached && !_preferences.ContinueAudioWhenHidden) await _library.PauseAsync(ct);
            _ready = true; VolumeSlider.Value = _library.Snapshot.Playback.Volume;
        }, initializing: true);
        if (_ready) VolumeSlider.Value = _library.Snapshot.Playback.Volume;
        _timer.Start();
        QueueRoamingTick();
    }

    /// <summary>Applies the DPAPI-stored credentials. Confirmed expired credentials are removed from disk;
    /// temporary service failures keep the file and retry without blocking the local library.</summary>
    private async Task RestoreAccountAsync(CancellationToken ct)
    {
        _accountRestorePending = false;
        var stored = _authStore.Load();
        if (string.IsNullOrEmpty(stored)) return;
        try { await _client.ImportCookiesAsync(stored, ct); }
        catch (ContentAccessException ex) when (ex.Kind is ContentFailureKind.AuthenticationRequired or ContentFailureKind.InvalidInput)
        { _authStore.Clear(); }
        catch (ContentAccessException)
        {
            // Failed verification says nothing about the stored credentials' validity.
            _accountRestorePending = true;
            _accountRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        RenderAccount();
    }

    private void RenderAccount()
    {
        if (_closing) return;
        var account = _client.CurrentAccount();
        AccountText.Text = account is null ? _accountRestorePending ? "登录验证待重试" : "未登录"
            : BilibiliCookieText.Describe(account) + (account.IsVip ? " · 大会员" : "");
        LoginButton.Content = account is null ? "登录" : "退出登录";
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_ready || _closing) return;
        if (_client.CurrentAccount() is not null)
        {
            // Leaving WallBili only: the all-site session stays valid for browsers and other devices.
            _authStore.Clear();
            await RunAsync(async ct =>
            {
                _accountRestorePending = false;
                await _client.SignOutAsync();
                await SynchronizeRoamingAccountAsync(ct);
                await ReloadCurrentForAccountAsync(ct);
            });
            RenderAccount();
            return;
        }
        // The automatic restore must not race a login the user is completing in the dialog.
        var restorePending = _accountRestorePending;
        _accountRestorePending = false;
        var dialog = new LoginDialog(_client) { Owner = this };
        dialog.ShowDialog();
        if (dialog.CookieResult is not { } cookie)
        {
            _accountRestorePending = restorePending;
            RenderAccount();
            return;
        }
        _authStore.Save(cookie);
        RenderAccount();
        await RunAsync(async ct =>
        {
            await SynchronizeRoamingAccountAsync(ct);
            await ReloadCurrentForAccountAsync(ct);
        }, "登录成功，正在刷新可用画质…");
        QueueRoamingTick();
    }

    /// <summary>After a login or logout the playing or paused current item is resolved again with the new
    /// session; a stopped queue only keeps its settings, nothing starts playing by itself.</summary>
    private async Task ReloadCurrentForAccountAsync(CancellationToken ct)
    {
        if (_library.Snapshot.Playback.Intent != PlaybackIntent.Stopped && _library.Snapshot.Current is not null)
            await _library.ReloadAsync(ct);
    }

    private void RefreshSettings()
    {
        _rendering = true;
        try
        {
            WallpaperBox.IsChecked = _preferences.WallpaperEnabled;
            ContinueAudioBox.IsChecked = _preferences.ContinueAudioWhenHidden;
            FitBox.SelectedIndex = _preferences.FillWindow ? 0 : 1;
            DisplayBox.ItemsSource = new[] { new DisplayTarget("", 0, 0, 0, 0, false) }
                .Concat(DesktopVideoHost.GetDisplays()).ToArray();
            DisplayBox.SelectedItem = DisplayBox.Items.Cast<DisplayTarget>().FirstOrDefault(d => d.Id == _preferences.DisplayId)
                ?? DisplayBox.Items[0];
            try { StartupBox.IsChecked = LoginStartup.IsEnabled(); }
            catch (System.Security.SecurityException) { StartupBox.IsChecked = false; }
        }
        finally { _rendering = false; }
    }

    private void RefreshDesktop()
    {
        if (_closing) return;
        var snapshot = _library.Snapshot;
        if (_host.Refresh(_preferences.WallpaperEnabled && !_suspended, _preferences.DisplayId,
            snapshot.OutputActive))
        {
            Interlocked.Exchange(ref _outputHandle, _host.Handle.ToInt64());
            _outputRebindNeeded = true;
        }
        DesktopStatus.Text = _host.Status;
    }

    private Task MaintainAsync() => RunAsync(async ct =>
    {
        if (_powerSuspendRequested)
        {
            _powerSuspendRequested = false; _suspended = true;
            await _library.SuspendAsync(ct);
        }
        if (_powerResumeRequested)
        {
            _powerResumeRequested = false; _suspended = false;
            RefreshSettings(); RefreshDesktop();
            await _library.ResumeAsync(ct);
            _outputRebindNeeded = false;
        }
        RefreshDesktop();
        if (_suspended) return;
        if (!_host.Attached && !_preferences.ContinueAudioWhenHidden && _library.Snapshot.Playback.Intent == PlaybackIntent.Playing)
            await _library.PauseAsync(ct);
        if (_outputRebindNeeded)
        {
            _outputRebindNeeded = false;
            await _library.RebindOutputAsync(ct);
        }
        var immediate = _networkChanged;
        _networkChanged = false;
        if (_accountRestorePending && (immediate || DateTimeOffset.UtcNow >= _accountRetryAt))
        {
            await RestoreAccountAsync(ct);
            if (_client.CurrentAccount() is not null)
            {
                await SynchronizeRoamingAccountAsync(ct);
                await ReloadCurrentForAccountAsync(ct);
            }
        }
        await _library.CheckpointAsync(ct);
        if (_library.Snapshot.RecoveryPending) await _library.RetryRecoveryAsync(immediate, ct);
    });

    private void OnSystemChange(Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        action();
        if (_ready && !_busy) _ = MaintainAsync();
    });
    private void DisplaySettingsChanged(object? sender, EventArgs e) => OnSystemChange(RefreshSettings);
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => OnSystemChange(() =>
    {
        _roaming.ResetClock();
        if (e.Mode == PowerModes.Suspend) _powerSuspendRequested = true;
        if (e.Mode == PowerModes.Resume) _powerResumeRequested = true;
    });
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) OnSystemChange(() => _networkChanged = true);
    }
    private void NetworkAddressChanged(object? sender, EventArgs e) => OnSystemChange(() => _networkChanged = true);

    private async void ApplyDesktop_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var updated = _preferences with
        {
            WallpaperEnabled = WallpaperBox.IsChecked == true,
            ContinueAudioWhenHidden = ContinueAudioBox.IsChecked == true,
            DisplayId = (DisplayBox.SelectedItem as DisplayTarget)?.Id,
            FillWindow = FitBox.SelectedIndex == 0
        };
        updated.Save(_preferencesPath);
        _preferences = updated;
        LoginStartup.SetEnabled(StartupBox.IsChecked == true);
        await _library.SetVideoFitAsync(updated.FillWindow, ct);
        RefreshDesktop();
        if (!_host.Attached && !updated.ContinueAudioWhenHidden) await _library.PauseAsync(ct);
    });

    private void ShowControls() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();
    private void RequestExit() { _exitRequested = true; Close(); }
    internal void ExitForSession() => RequestExit();
    private async void TogglePause() => await RunAsync(ct => _library.Snapshot.Playback.Intent == PlaybackIntent.Playing
        ? _library.PauseAsync(ct) : _library.PlayAsync(token: ct));
    private void ToggleWallpaper()
    {
        if (_busy || !_ready) return;
        WallpaperBox.IsChecked = !_preferences.WallpaperEnabled;
        ApplyDesktop_Click(this, new());
    }
    private async void ImportVideo_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ct => _library.ImportVideoAsync(VideoInput.Text.Trim(), ct), "正在导入视频及分 P…");
    private async void ImportCollection_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ct => _library.ImportCollectionAsync(BilibiliCollectionInput.Parse(CollectionInput.Text), ct), "正在读取合集全部分页与分 P…");
    private async void ImportIds_Click(object sender, RoutedEventArgs e) => await RunAsync(ct =>
    {
        if (!long.TryParse(UploaderInput.Text, out var uid) || uid <= 0 ||
            !long.TryParse(CollectionIdInput.Text, out var id) || id <= 0) throw new ArgumentException();
        return _library.ImportCollectionAsync(new(uid, id, KindBox.SelectedIndex == 0 ? CollectionKind.Season : CollectionKind.Series), ct);
    }, "正在读取合集全部分页与分 P…");
    private async void RefreshSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesBox.SelectedItem is SourceRow { Id.Length: > 0 } source)
            await RunAsync(ct => _library.RefreshSourceAsync(source.Id, ct), "正在刷新来源，播放将继续…");
    }
    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesBox.SelectedItem is SourceRow { Id.Length: > 0 } source)
            await RunAsync(ct => _library.RemoveSourceAsync(source.Id, ct));
    }
    private async void Enqueue_Click(object sender, RoutedEventArgs e)
    {
        var selected = CatalogGrid.SelectedItems.Cast<EntryRow>().Select(row => row.Entry.Key).ToHashSet();
        var keys = CatalogGrid.Items.Cast<EntryRow>().Where(row => selected.Contains(row.Entry.Key)).Select(row => row.Entry.Key).ToArray();
        await RunAsync(ct => _library.EnqueueAsync(keys, ct));
    }
    private async void EnqueueAll_Click(object sender, RoutedEventArgs e)
    {
        var keys = CatalogGrid.Items.Cast<EntryRow>().Select(row => row.Entry.Key).ToArray();
        await RunAsync(ct => _library.EnqueueAsync(keys, ct));
    }
    private async void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        var keys = CatalogGrid.SelectedItems.Cast<EntryRow>().Select(row => row.Entry.Key).ToArray();
        await RunAsync(async ct => { foreach (var key in keys) await _library.RemoveEntryAsync(key, ct); });
    }
    private async void EntryUp_Click(object sender, RoutedEventArgs e) => await MoveEntryAsync(-1);
    private async void EntryDown_Click(object sender, RoutedEventArgs e) => await MoveEntryAsync(1);
    private async Task MoveEntryAsync(int offset)
    {
        if (CatalogGrid.SelectedItem is not EntryRow row) return;
        var visible = CatalogGrid.Items.Cast<EntryRow>().ToList();
        var index = visible.IndexOf(row);
        var target = Math.Clamp(index + offset, 0, visible.Count - 1);
        var all = _library.Snapshot.Entries.ToList();
        var delta = all.FindIndex(e => e.Key == visible[target].Entry.Key) - all.FindIndex(e => e.Key == row.Entry.Key);
        await RunAsync(ct => _library.MoveEntryAsync(row.Entry.Key, delta, ct));
    }
    private async void QueueUp_Click(object sender, RoutedEventArgs e) => await MoveQueueAsync(-1);
    private async void QueueDown_Click(object sender, RoutedEventArgs e) => await MoveQueueAsync(1);
    private async Task MoveQueueAsync(int offset)
    {
        if (_library.Snapshot.Source == PlaybackSource.Roaming) return;
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.MoveQueueAsync(row.Id, offset, ct));
    }
    private async void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Snapshot.Source == PlaybackSource.Roaming) return;
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.RemoveQueueAsync(row.Id, ct));
    }
    private async void PlaySelected_Click(object sender, RoutedEventArgs e) => await PlaySelectedAsync();
    private async void Queue_DoubleClick(object sender, MouseButtonEventArgs e) => await PlaySelectedAsync();
    private async Task PlaySelectedAsync()
    {
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.PlayManualAsync(row.Id, ct), "正在解析并播放…");
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.PlayAsync(token: ct));
    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.PauseAsync(ct));
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.StopAsync(ct));
    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.PreviousAsync(ct));
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.NextAsync(ct));
    private async void Reload_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.ReloadAsync(ct));
    private async void Seek_Click(object sender, RoutedEventArgs e) => await RunAsync(ct =>
    {
        if (!double.TryParse(SeekInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) throw new ArgumentException();
        return _library.SeekAsync(TimeSpan.FromSeconds(seconds), ct);
    });
    private async void Volume_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => _library.SetVolumeAsync(VolumeSlider.Value, ct));
    private async void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || !_ready) return;
        await RunAsync(ct => _library.SetModeAsync(ModeBox.SelectedIndex == 0 ? QueueMode.Sequential : QueueMode.RepeatAll, ct));
    }
    private void Sources_Changed(object sender, SelectionChangedEventArgs e) { if (!_rendering && _ready) Render(); }

    private static bool Matches(CatalogEntry entry, string query) =>
        entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Uploader.Contains(query, StringComparison.OrdinalIgnoreCase);

    // 左侧导航与分页容器是同一份状态：导航项的 Tag 即 ContentTabs 的页索引。
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        if (ContentTabs is { } tabs && index >= 0 && index < tabs.Items.Count) tabs.SelectedIndex = index;
        if (index == 2) VideoInput?.Focus();
    }

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e) => RenderNav();

    private void RenderNav()
    {
        if (ContentTabs is null) return;
        var active = ContentTabs.SelectedIndex;
        var activeStyle = TryFindResource("Btn.NavActive") as Style;
        var idleStyle = TryFindResource("Btn.Nav") as Style;
        foreach (var button in NavButtons())
        {
            // 「添加来源」只是跳转入口，不参与选中态。
            var highlighted = button.Name != "NavAddSourceButton"
                && button.Tag is string tag && int.TryParse(tag, out var target) && target == active;
            var style = highlighted ? activeStyle : idleStyle;
            if (style is not null) button.Style = style;
        }
    }

    private IEnumerable<Button> NavButtons()
    {
        foreach (var panel in new[] { NavPanel, NavPanelSecondary })
        {
            if (panel is null) continue;
            foreach (var child in panel.Children) if (child is Button button) yield return button;
        }
    }

    private void Render()
    {
        if (_closing) return;
        _rendering = true;
        try
        {
            var snapshot = _library.Snapshot;
            var selectedSource = (SourcesBox.SelectedItem as SourceRow)?.Id ?? "";
            var selectedEntries = CatalogGrid.SelectedItems.Cast<EntryRow>().Select(r => r.Entry.Key).ToHashSet();
            var selectedQueue = (QueueGrid.SelectedItem as QueueRow)?.Id;
            SourcesBox.ItemsSource = new[] { new SourceRow("", $"全部视频（{snapshot.Entries.Count}）") }
                .Concat(snapshot.Sources.Select(s => new SourceRow(s.Id, $"{s.Title}（{s.Members.Count}）"))).ToArray();
            SourcesBox.SelectedItem = SourcesBox.Items.Cast<SourceRow>().FirstOrDefault(s => s.Id == selectedSource) ?? SourcesBox.Items[0];
            var source = snapshot.Sources.FirstOrDefault(s => s.Id == (SourcesBox.SelectedItem as SourceRow)?.Id);
            // 顶栏筛选只在片库与队列的渲染里生效，空关键字等同于不过滤。
            var query = SearchBox?.Text?.Trim() ?? "";
            CatalogGrid.ItemsSource = snapshot.Entries
                .Where(e => source is null || source.Members.Contains(e.Key))
                .Where(e => query.Length == 0 || Matches(e, query))
                .Select(e => new EntryRow(e, Availability(e))).ToArray();
            foreach (var row in CatalogGrid.Items.Cast<EntryRow>()) if (selectedEntries.Contains(row.Entry.Key)) CatalogGrid.SelectedItems.Add(row);
            var entries = snapshot.Entries.ToDictionary(e => e.Key);
            var manualQueue = snapshot.ManualQueue ?? snapshot.Queue;
            var manualPlayback = snapshot.ManualPlayback ?? snapshot.Playback;
            var queueRows = manualQueue.Select(q => new QueueRow(q.Id,
                q.Id == manualPlayback.CurrentId ? "▶" : "", entries[q.Key].Title, Availability(entries[q.Key]))).ToArray();
            QueueGrid.ItemsSource = queueRows;
            QueueGrid.SelectedItem = queueRows.FirstOrDefault(q => q.Id == selectedQueue);
            QueueTitle.Text = $"手动队列（{manualQueue.Count}）";
            // 「接下来播放」是队列的预览，跳过正在播放的那一项。
            if (UpcomingBox is not null) UpcomingBox.ItemsSource = queueRows.Where(r => r.Marker.Length == 0).Take(6).ToArray();
            var intent = snapshot.Playback.Intent switch { PlaybackIntent.Playing => "播放中", PlaybackIntent.Paused => "已暂停", _ => "已停止" };
            CurrentText.Text = snapshot.Current is { } current ? $"{intent} · {current.Title}" : "尚未选择播放项";
            var duration = snapshot.Current?.Duration ?? TimeSpan.Zero;
            var position = TimeSpan.FromSeconds(snapshot.Playback.PositionSeconds);
            PositionText.Text = $"{Format(position)} / {Format(duration)}";
            Progress.Maximum = Math.Max(1, duration.TotalSeconds);
            Progress.Value = Math.Clamp(position.TotalSeconds, 0, Progress.Maximum);
            ModeBox.SelectedIndex = snapshot.Playback.Mode == QueueMode.Sequential ? 0 : 1;
            StatusText.Text = snapshot.Notice;
            RenderQuality(snapshot);
            RenderRoaming();
            _tray.SetStatus(snapshot.Current?.Title ?? intent);
            RefreshDesktop();
        }
        finally { _rendering = false; }
    }

    /// <summary>Shows the offered tiers as selections and the actually playing quality separately, so a
    /// fallback never leaves the interface claiming the target tier is playing.</summary>
    private void RenderQuality(LibrarySnapshot snapshot)
    {
        var media = snapshot.Media;
        var videoRows = new List<QualityRow> { new(null, "自动：最高可用") };
        var audioRows = new List<QualityRow> { new(null, "自动：最高普通音质") };
        if (media is not null)
        {
            videoRows.AddRange(media.VideoOptions.Select(o => new QualityRow(o.Id, o.Label)));
            audioRows.AddRange(media.AudioOptions.Select(o => new QualityRow(o.Id, o.Label, o.Special)));
        }
        // A persisted preference may target a tier this video does not offer; keep it visible instead of hiding it.
        if (_preferences.PreferredVideoQualityId is { } videoId && videoRows.All(r => r.Id != videoId))
            videoRows.Add(new(videoId, $"已保存画质档位 {videoId}"));
        if (_preferences.PreferredAudioQualityId is { } audioId && audioRows.All(r => r.Id != audioId))
            audioRows.Add(new(audioId, $"已保存音质档位 {audioId}"));
        VideoQualityBox.ItemsSource = videoRows;
        VideoQualityBox.SelectedItem = videoRows.FirstOrDefault(r => r.Id == _preferences.PreferredVideoQualityId) ?? videoRows[0];
        AudioQualityBox.ItemsSource = audioRows;
        AudioQualityBox.SelectedItem = audioRows.FirstOrDefault(r => r.Id == _preferences.PreferredAudioQualityId) ?? audioRows[0];
        var noAudio = media is { NoAudio: true };
        AudioQualityBox.IsEnabled = !noAudio;
        QualityStatusText.Text = media is null
            ? snapshot.Current is null ? "" : "正在获取可用画质…"
            : noAudio
                ? $"实际播放：{media.SelectedVideo}；音轨：无音轨"
                : media.EmbeddedAudio
                    ? $"实际播放：{media.SelectedVideo}；音轨：内嵌（不可单独选择）"
                    : $"实际播放：{media.SelectedVideo} / {media.SelectedAudio}" +
                      (media.VideoFallback is { } v ? $"；{v}" : "") +
                      (media.AudioFallback is { } a ? $"；{a}" : "");
    }

    private async void VideoQuality_Changed(object sender, SelectionChangedEventArgs e) => await ApplyQualityPreferenceAsync();
    private async void AudioQuality_Changed(object sender, SelectionChangedEventArgs e) => await ApplyQualityPreferenceAsync();

    /// <summary>Saves the global preference and switches the current stream when one is active. The combo
    /// shows the preference; the actually playing quality and any fallback appear in the status text.</summary>
    private async Task ApplyQualityPreferenceAsync()
    {
        if (_rendering || !_ready || _closing) return;
        var selection = ToSelection();
        if (selection == ToSelection(_preferences)) return;
        var updated = _preferences with
        {
            PreferredVideoQualityId = selection.VideoQualityId,
            PreferredAudioQualityId = selection.AudioQualityId
        };
        updated.Save(_preferencesPath);
        _preferences = updated;
        await RunAsync(ct => _library.SetMediaSelectionAsync(selection, ct));
    }

    // Saved preferences must not read the controls: SelectionChanged already contains the new values.
    // A selected automatic row has a null id and must remain null instead of reviving a saved tier.
    private MediaSelectionPreferences ToSelection(DesktopPreferences? preferences = null) => preferences is { } saved
        ? new(saved.PreferredVideoQualityId, saved.PreferredAudioQualityId)
        : new((VideoQualityBox?.SelectedItem as QualityRow)?.Id, (AudioQualityBox?.SelectedItem as QualityRow)?.Id);

    private async Task RunAsync(Func<CancellationToken, Task> operation, string? notice = null, bool initializing = false)
    {
        if (_busy || _closing || (!_ready && !initializing)) return;
        _busy = true;
        Volatile.Write(ref _mpvPath, string.IsNullOrWhiteSpace(MpvPathInput.Text) ? "mpv" : MpvPathInput.Text.Trim());
        ImportPanel.IsEnabled = ListsPanel.IsEnabled = PlayerPanel.IsEnabled = DesktopPanel.IsEnabled = false;
        LoginButton.IsEnabled = false;
        if (notice is not null) StatusText.Text = notice;
        try
        {
            if (_preferences.MpvPath != _mpvPath)
            {
                _preferences = _preferences with { MpvPath = _mpvPath };
                _preferences.Save(_preferencesPath);
            }
            _pending = operation(_lifetime.Token); await _pending; Render();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex switch
            {
                ContentAccessException content => $"来源访问失败（{Failure(content.Kind.ToString())}），请检查输入或稍后重试。",
                MpvException player => $"播放器错误（{player.Code}），请检查 mpv 路径后重试。",
                SqliteException or IOException or UnauthorizedAccessException => "本地资料读写失败，请检查磁盘空间及应用数据目录权限。",
                System.Security.SecurityException => "登录启动设置无法写入，请检查当前用户的注册表权限。",
                ArgumentException or FormatException => "输入无效，请检查链接、ID 或跳转秒数。",
                OperationCanceledException or HttpRequestException => "网络请求失败或超时，请稍后重试。",
                _ => "操作未完成，请检查来源与播放器配置后重试。"
            };
        }
        finally
        {
            _busy = false;
            _ready = _library.IsInitialized;
            ImportPanel.IsEnabled = ListsPanel.IsEnabled = PlayerPanel.IsEnabled = DesktopPanel.IsEnabled = _ready && !_closing;
            LoginButton.IsEnabled = _ready && !_closing;
            RenderRoaming();
        }
    }

    private static string Availability(CatalogEntry entry) => entry.Availability switch
    {
        EntryAvailability.Available => "可播放", EntryAvailability.Failed => "失败：" + Failure(entry.Failure), _ => "待播放"
    };
    private static string Failure(string? failure) => failure switch
    {
        "Removed" => "已失效", "AuthenticationRequired" => "需要权限", "Network" => "网络错误",
        "RateLimited" => "请求频繁", "Unsupported" or "MediaFailed" => "播放失败", "Inaccessible" => "不可访问", _ => "访问错误"
    };
    private static string Format(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (!_exitRequested) { Hide(); return; }
        if (_closing) return;
        _closing = true;
        _timer.Stop(); _lifetime.Cancel();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        IsEnabled = false;
        try
        {
            try { await _pending; } catch { }
            await _roaming.DisposeAsync();
            try { await Task.WhenAll(_roamingOperations.ToArray()); } catch { }
            await _library.DisposeAsync();
        }
        catch
        {
            MessageBox.Show("最后一次进度未能保存，将使用上次保存的进度恢复。", "WallBili", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _tray.Dispose(); _host.Dispose(); _http.Dispose(); _lifetime.Dispose(); _closed = true; Close();
            Application.Current.Shutdown();
        }
    }

    private sealed record SourceRow(string Id, string Title);
    private sealed record EntryRow(CatalogEntry Entry, string Status);
    private sealed record QueueRow(Guid Id, string Marker, string Title, string Status);
    private sealed record QualityRow(string? Id, string Label, bool Special = false);
}
