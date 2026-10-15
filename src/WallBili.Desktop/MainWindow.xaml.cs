using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using WallBili.Infrastructure.Storage;
using WallBili.Player.Mpv;

namespace WallBili.Desktop;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly LibrarySession _library;
    private Task _pending = Task.CompletedTask;
    private bool _busy, _closing, _closed, _rendering, _ready;
    private string _mpvPath;
    private string _savedMpvPath;
    private readonly string _preferencesPath;

    public MainWindow()
    {
        InitializeComponent();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallBili");
        _preferencesPath = Path.Combine(directory, "preferences.json");
        string? savedPath = null;
        try
        {
            if (File.Exists(_preferencesPath)) savedPath = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_preferencesPath))?.MpvPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        _mpvPath = string.IsNullOrWhiteSpace(savedPath) ? Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv" : savedPath;
        _savedMpvPath = _mpvPath;
        MpvPathInput.Text = _mpvPath;
        var player = new MpvQueuePlayer(() => new() { ExecutablePath = Volatile.Read(ref _mpvPath) });
        _library = new(new BilibiliClient(_http), new SqliteLibraryStore(Path.Combine(directory, "library.db")), player);
        _library.Changed += (_, _) => _ = Dispatcher.InvokeAsync(Render);
        _timer.Tick += async (_, _) =>
        {
            if (!_busy && !_closing && _ready) await RunAsync(ct => _library.CheckpointAsync(ct));
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(async ct => { await _library.InitializeAsync(ct); _ready = true; VolumeSlider.Value = _library.Snapshot.Playback.Volume; }, initializing: true);
        if (_ready) VolumeSlider.Value = _library.Snapshot.Playback.Volume;
        _timer.Start();
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
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.MoveQueueAsync(row.Id, offset, ct));
    }
    private async void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.RemoveQueueAsync(row.Id, ct));
    }
    private async void PlaySelected_Click(object sender, RoutedEventArgs e) => await PlaySelectedAsync();
    private async void Queue_DoubleClick(object sender, MouseButtonEventArgs e) => await PlaySelectedAsync();
    private async Task PlaySelectedAsync()
    {
        if (QueueGrid.SelectedItem is QueueRow row) await RunAsync(ct => _library.PlayAsync(row.Id, ct), "正在解析并播放…");
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
            CatalogGrid.ItemsSource = snapshot.Entries.Where(e => source is null || source.Members.Contains(e.Key))
                .Select(e => new EntryRow(e, Availability(e))).ToArray();
            foreach (var row in CatalogGrid.Items.Cast<EntryRow>()) if (selectedEntries.Contains(row.Entry.Key)) CatalogGrid.SelectedItems.Add(row);
            var entries = snapshot.Entries.ToDictionary(e => e.Key);
            QueueGrid.ItemsSource = snapshot.Queue.Select(q => new QueueRow(q.Id,
                q.Id == snapshot.Playback.CurrentId ? "▶" : "", entries[q.Key].Title, Availability(entries[q.Key]))).ToArray();
            QueueGrid.SelectedItem = QueueGrid.Items.Cast<QueueRow>().FirstOrDefault(q => q.Id == selectedQueue);
            QueueTitle.Text = $"播放队列（{snapshot.Queue.Count}）";
            var intent = snapshot.Playback.Intent switch { PlaybackIntent.Playing => "播放中", PlaybackIntent.Paused => "已暂停", _ => "已停止" };
            CurrentText.Text = snapshot.Current is { } current ? $"{intent} · {current.Title}" : "尚未选择播放项";
            var duration = snapshot.Current?.Duration ?? TimeSpan.Zero;
            var position = TimeSpan.FromSeconds(snapshot.Playback.PositionSeconds);
            PositionText.Text = $"{Format(position)} / {Format(duration)}";
            Progress.Maximum = Math.Max(1, duration.TotalSeconds);
            Progress.Value = Math.Clamp(position.TotalSeconds, 0, Progress.Maximum);
            ModeBox.SelectedIndex = snapshot.Playback.Mode == QueueMode.Sequential ? 0 : 1;
            StatusText.Text = snapshot.Notice;
        }
        finally { _rendering = false; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, string? notice = null, bool initializing = false)
    {
        if (_busy || _closing || (!_ready && !initializing)) return;
        _busy = true;
        Volatile.Write(ref _mpvPath, string.IsNullOrWhiteSpace(MpvPathInput.Text) ? "mpv" : MpvPathInput.Text.Trim());
        ImportPanel.IsEnabled = ListsPanel.IsEnabled = PlayerPanel.IsEnabled = false;
        if (notice is not null) StatusText.Text = notice;
        try
        {
            if (_savedMpvPath != _mpvPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
                var temporary = _preferencesPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new DesktopPreferences(_mpvPath)));
                File.Move(temporary, _preferencesPath, overwrite: true);
                _savedMpvPath = _mpvPath;
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
                ArgumentException or FormatException => "输入无效，请检查链接、ID 或跳转秒数。",
                OperationCanceledException or HttpRequestException => "网络请求失败或超时，请稍后重试。",
                _ => "操作未完成，请检查来源与播放器配置后重试。"
            };
        }
        finally
        {
            _busy = false;
            _ready = _library.IsInitialized;
            ImportPanel.IsEnabled = ListsPanel.IsEnabled = PlayerPanel.IsEnabled = _ready && !_closing;
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
        if (_closing) return;
        _closing = true;
        _timer.Stop(); _lifetime.Cancel();
        IsEnabled = false;
        try
        {
            try { await _pending; } catch { }
            await _library.DisposeAsync();
        }
        catch
        {
            MessageBox.Show("最后一次进度未能保存，将使用上次保存的进度恢复。", "WallBili", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _http.Dispose(); _lifetime.Dispose(); _closed = true; Close(); }
    }

    private sealed record SourceRow(string Id, string Title);
    private sealed record EntryRow(CatalogEntry Entry, string Status);
    private sealed record QueueRow(Guid Id, string Marker, string Title, string Status);
    private sealed record DesktopPreferences(string MpvPath);
}
