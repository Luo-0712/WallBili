using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;

namespace WallBili.Desktop.Presentation;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly LibrarySession _library;
    private readonly Dispatcher _dispatcher;
    private readonly Func<DesktopPreferences, bool, Task> _saveSettings;
    private readonly Dictionary<ContentKey, VideoRow> _videos = [];
    private readonly Dictionary<Guid, QueueRow> _queue = [];
    private LibrarySnapshot _snapshot;
    private DesktopPreferences _preferences;
    private bool _busy, _importOpen, _sourcesTab, _wallpaperEnabled, _continueAudio, _autoStart, _fillWindow, _isSeeking;
    private string _page = "Workspace", _search = "", _sourceFilter = "", _statusFilter = "", _notice = "", _account = "未登录";
    private string _importMode = "video", _importInput = "", _uploaderId = "", _collectionId = "", _collectionKind = "season", _importError = "";
    private string _mpvPath = "", _videoId = "", _audioId = "", _settingsStatus = "", _desktopStatus = "桌面画面待命";
    private double _seekPosition, _volume;
    private QueueRow? _selectedQueue;
    private DisplayChoice? _display;
    private bool _refreshing;

    internal ShellViewModel(LibrarySession library, DesktopPreferences preferences, Func<DesktopPreferences, bool, Task>? saveSettings = null, bool preview = false)
    {
        _library = library; _snapshot = library.Snapshot; _preferences = preferences;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _saveSettings = saveSettings ?? ((_, _) => Task.CompletedTask);
        IsPreview = preview;
        _wallpaperEnabled = preferences.WallpaperEnabled; _continueAudio = preferences.ContinueAudioWhenHidden;
        _fillWindow = preferences.FillWindow; _mpvPath = preferences.MpvPath;
        _videoId = preferences.PreferredVideoQualityId ?? ""; _audioId = preferences.PreferredAudioQualityId ?? "";

        NavigateCommand = new UiCommand(p => Navigate(p as string ?? "Workspace"));
        OpenImportCommand = new UiCommand(_ => { ImportError = ""; IsImportOpen = true; });
        CloseImportCommand = new UiCommand(_ => IsImportOpen = false, _ => !IsBusy);
        SetImportModeCommand = new UiCommand(p => { ImportMode = p as string ?? "video"; ImportError = ""; }, _ => !IsBusy);
        ImportCommand = Action(_ => ImportAsync());
        PlayPauseCommand = Action(_ => _snapshot.Playback.Intent == PlaybackIntent.Playing ? _library.PauseAsync() : _library.PlayAsync(), _ => HasQueue);
        NextCommand = Action(_ => _library.NextAsync(), _ => HasQueue);
        PreviousCommand = Action(_ => _library.PreviousAsync(), _ => HasQueue);
        StopCommand = Action(_ => _library.StopAsync(), _ => HasCurrent);
        ReloadCommand = Action(_ => _library.ReloadAsync(), _ => HasCurrent);
        RetryCommand = Action(_ => _library.RetryRecoveryAsync(true), _ => _snapshot.RecoveryPending);
        PlayVideoCommand = Action(p => PlayVideoAsync((VideoRow)p!), p => p is VideoRow);
        AddVideoCommand = Action(p => _library.EnqueueAsync([((VideoRow)p!).Key]), p => p is VideoRow);
        AddSelectedCommand = Action(_ => _library.EnqueueAsync(SelectedVideos().Select(v => v.Key)), _ => SelectedCount > 0);
        AddAllCommand = Action(_ => _library.EnqueueAsync(CatalogRows.Select(v => v.Key)), _ => CatalogRows.Count > 0);
        MoveVideosUpCommand = Action(_ => MoveVideosAsync(-1), _ => SelectedCount > 0);
        MoveVideosDownCommand = Action(_ => MoveVideosAsync(1), _ => SelectedCount > 0);
        DeleteVideosCommand = Action(_ => DeleteVideosAsync(), _ => SelectedCount > 0);
        ClearSelectionCommand = new UiCommand(_ => { foreach (var row in CatalogRows) row.IsSelected = false; });
        SelectAllCommand = new UiCommand(_ => { var select = !AllVideosSelected; foreach (var row in CatalogRows) row.IsSelected = select; });
        FilterSourceCommand = new UiCommand(p => { SourceFilter = p as string ?? ""; ShowSources = false; Navigate("Library"); });
        RefreshSourceCommand = Action(p => _library.RefreshSourceAsync(((SourceRow)p!).Id), p => p is SourceRow);
        RemoveSourceCommand = Action(p => _library.RemoveSourceAsync(((SourceRow)p!).Id), p => p is SourceRow);
        AddSourceCommand = Action(p => _library.EnqueueAsync(((SourceRow)p!).Members), p => p is SourceRow { Count: > 0 });
        PlayQueueCommand = Action(p => _library.PlayAsync((p as QueueRow ?? SelectedQueue)?.Id), _ => HasQueue);
        MoveQueueUpCommand = Action(_ => _library.MoveQueueAsync(SelectedQueue!.Id, -1), _ => SelectedQueue is not null);
        MoveQueueDownCommand = Action(_ => _library.MoveQueueAsync(SelectedQueue!.Id, 1), _ => SelectedQueue is not null);
        RemoveQueueCommand = Action(p => _library.RemoveQueueAsync((p as QueueRow ?? SelectedQueue)!.Id), p => p is QueueRow || SelectedQueue is not null);
        SeekCommand = Action(_ => CommitSeekAsync(), _ => HasCurrent);
        VolumeCommand = Action(_ => _library.SetVolumeAsync(Volume));
        ChangeModeCommand = Action(p => _library.SetModeAsync((p as string ?? ModeId) == "repeat" ? QueueMode.RepeatAll : QueueMode.Sequential));
        ApplySettingsCommand = Action(_ => SaveSettingsAsync());
        ToggleWallpaperCommand = Action(async _ => { IsWallpaperEnabled = !IsWallpaperEnabled; await SaveSettingsAsync(); });
        ApplyQualityCommand = Action(_ => SaveQualityAsync());
        LoginCommand = new UiCommand(_ => LoginRequested?.Invoke(), _ => !IsBusy);
        SignOutCommand = new UiCommand(_ => SignOutRequested?.Invoke(), _ => !IsBusy && IsSignedIn);
        OpenMcpSettingsCommand = new UiCommand(_ => McpSettingsRequested?.Invoke(), _ => !IsBusy && !IsPreview);
        OpenPlaylistsCommand = new UiCommand(_ => PlaylistsRequested?.Invoke(), _ => !IsBusy);
        OpenRoamingCommand = new UiCommand(_ => RoamingRequested?.Invoke(), _ => !IsBusy && !IsPreview);
        _library.Changed += LibraryChanged;
        Refresh();
    }

    public event Action? LoginRequested;
    public event Action? SignOutRequested;
    public event Action? McpSettingsRequested;
    public event Action? PlaylistsRequested;
    public event Action? RoamingRequested;
    public UiCommand OpenMcpSettingsCommand { get; }
    public UiCommand OpenPlaylistsCommand { get; }
    public UiCommand OpenRoamingCommand { get; }
    public bool IsPreview { get; }
    public ObservableCollection<VideoRow> CatalogRows { get; } = [];
    public ObservableCollection<QueueRow> QueueRows { get; } = [];
    public ObservableCollection<QueueRow> UpcomingRows { get; } = [];
    public ObservableCollection<SourceRow> SourceRows { get; } = [];
    public ObservableCollection<SelectOption> SourceOptions { get; } = [];
    public ObservableCollection<SelectOption> VideoOptions { get; } = [];
    public ObservableCollection<SelectOption> AudioOptions { get; } = [];
    public IReadOnlyList<SelectOption> StatusOptions { get; } = [new("", "全部状态"), new("ready", "可播放"), new("failed", "不可访问")];
    public IReadOnlyList<SelectOption> ModeOptions { get; } = [new("repeat", "列表循环"), new("sequential", "顺序播放")];
    public IReadOnlyList<SelectOption> KindOptions { get; } = [new("season", "合集"), new("series", "系列")];
    public IReadOnlyList<DisplayChoice> Displays { get; private set; } = [];

    public ICommand NavigateCommand { get; }
    public ICommand OpenImportCommand { get; }
    public ICommand CloseImportCommand { get; }
    public ICommand SetImportModeCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand PlayVideoCommand { get; }
    public ICommand AddVideoCommand { get; }
    public ICommand AddSelectedCommand { get; }
    public ICommand AddAllCommand { get; }
    public ICommand MoveVideosUpCommand { get; }
    public ICommand MoveVideosDownCommand { get; }
    public ICommand DeleteVideosCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand FilterSourceCommand { get; }
    public ICommand RefreshSourceCommand { get; }
    public ICommand RemoveSourceCommand { get; }
    public ICommand AddSourceCommand { get; }
    public ICommand PlayQueueCommand { get; }
    public ICommand MoveQueueUpCommand { get; }
    public ICommand MoveQueueDownCommand { get; }
    public ICommand RemoveQueueCommand { get; }
    public ICommand SeekCommand { get; }
    public ICommand VolumeCommand { get; }
    public ICommand ChangeModeCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand ToggleWallpaperCommand { get; }
    public ICommand ApplyQualityCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SignOutCommand { get; }

    public string Page => _page;
    public bool IsWorkspace => Page == "Workspace";
    public bool IsLibrary => Page == "Library";
    public bool IsQueue => Page == "Queue";
    public bool IsSettings => Page == "Settings";
    public bool CanAddFromHeader => IsWorkspace || IsLibrary;
    public string PageTitle => Page switch { "Library" => "我的片库", "Queue" => "播放队列", "Settings" => "桌面与后台", _ => "播放工作台" };
    public string PageSubtitle => Page switch
    {
        "Library" => $"已导入 {_snapshot.Entries.Count} 个分 P，来自 {_snapshot.Sources.Count} 个来源。",
        "Queue" => $"共 {_snapshot.Queue.Count} 项，双击条目可切换播放，队列顺序会自动保存。",
        "Settings" => "声音和画面，按你的使用习惯分别设置。",
        _ => "音乐在后台，桌面留给风景。"
    };
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(IsIdle), nameof(ImportButtonText)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsIdle => !IsBusy;
    public bool HasEntries => _snapshot.Entries.Count > 0;
    public bool IsEmpty => !HasEntries;
    public bool HasQueue => _snapshot.Queue.Count > 0;
    public bool QueueEmpty => !HasQueue;
    public bool HasCurrent => _snapshot.Current is not null;
    public bool IsPlaying => _snapshot.Playback.Intent == PlaybackIntent.Playing;
    public bool RecoveryPending => _snapshot.RecoveryPending;
    public bool ShowNotice => Notice.Length > 0;
    public bool NoFilterResults => HasEntries && CatalogRows.Count == 0;
    public bool NoQueueResults => HasQueue && QueueRows.Count == 0;
    public int QueueCount => _snapshot.Queue.Count;
    public int EntryCount => _snapshot.Entries.Count;
    public int SourceCount => _snapshot.Sources.Count;
    public string SourcesSummary => $"{SourceCount} 个来源 · {EntryCount} 个分 P";
    public string CurrentTitle => _snapshot.Current?.Title ?? "还没有开始播放";
    public string CurrentUploader => _snapshot.Current?.Uploader ?? "添加喜欢的音乐，随时回来继续听。";
    public string CurrentSource => _snapshot.Current is { } entry ? $"P{entry.Page:00} · {FindSource(entry.Key)}" : "从片库中选择一项开始";
    public string PlaybackStatus => DisplayText.Intent(_snapshot);
    public string PlayButtonLabel => IsPlaying ? "暂停" : "播放";
    public string PositionText => DisplayText.Time(_snapshot.Playback.PositionSeconds);
    public string DurationText => DisplayText.Time(_snapshot.Current?.Duration.TotalSeconds ?? 0);
    public double DurationSeconds => Math.Max(1, _snapshot.Current?.Duration.TotalSeconds ?? 1);
    public double SeekPosition { get => _seekPosition; set { if (Set(ref _seekPosition, value)) Changed(nameof(SeekText)); } }
    public string SeekText => DisplayText.Time(SeekPosition);
    public double Volume { get => _volume; set { if (Set(ref _volume, Math.Clamp(value, 0, 100))) Changed(nameof(VolumeText)); } }
    public string VolumeText => Math.Round(Volume).ToString(CultureInfo.InvariantCulture);
    public bool IsSeeking { get => _isSeeking; set => _isSeeking = value; }
    public bool IsAdjustingVolume { get; set; }
    public string ModeId => _snapshot.Playback.Mode == QueueMode.RepeatAll ? "repeat" : "sequential";
    public string ModeDescription => ModeId == "repeat" ? "播放到末尾后，从第一项继续。" : "播放到队列末尾后停止。";
    public string SearchText { get => _search; set { if (Set(ref _search, value)) RefreshRows(); } }
    public string SourceFilter { get => _sourceFilter; set { if (Set(ref _sourceFilter, value ?? "")) RefreshRows(); } }
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value ?? "")) RefreshRows(); } }
    public bool ShowSources { get => _sourcesTab; set => Set(ref _sourcesTab, value); }
    public int SelectedCount => CatalogRows.Count(r => r.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool AllVideosSelected => CatalogRows.Count > 0 && SelectedCount == CatalogRows.Count;
    public string SelectionText => $"已选择 {SelectedCount} 项";
    public QueueRow? SelectedQueue { get => _selectedQueue; set { if (Set(ref _selectedQueue, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) Changed(nameof(ShowNotice)); } }
    public string AccountText { get => _account; private set { if (Set(ref _account, value)) Changed(nameof(IsSignedIn)); } }
    public bool IsSignedIn => AccountText != "未登录";
    public string DesktopStatus { get => _desktopStatus; private set => Set(ref _desktopStatus, value); }
    public string DesktopHint => IsWallpaperEnabled ? "在所选显示器上显示当前视频。" : "画面已隐藏，音乐可以继续播放。";
    public string DisplayLabel => SelectedDisplay?.ShortLabel ?? "自动选择主屏";
    public string DisplayDescription => SelectedDisplay?.Label ?? "自动选择主屏";

    public bool IsWallpaperEnabled { get => _wallpaperEnabled; set { if (Set(ref _wallpaperEnabled, value)) Changed(nameof(DesktopHint)); } }
    public bool ContinueAudio { get => _continueAudio; set => Set(ref _continueAudio, value); }
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool FillWindow { get => _fillWindow; set => Set(ref _fillWindow, value); }
    public string MpvPath { get => _mpvPath; set => Set(ref _mpvPath, value); }
    public DisplayChoice? SelectedDisplay { get => _display; set { if (Set(ref _display, value)) Changed(nameof(DisplayLabel), nameof(DisplayDescription)); } }
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }
    public string VideoQualityId { get => _videoId; set => Set(ref _videoId, value ?? ""); }
    public string AudioQualityId { get => _audioId; set => Set(ref _audioId, value ?? ""); }
    public string QualityStatus => _snapshot.Media is { } media
        ? string.Join(" · ", new[] { media.VideoFallback, media.AudioFallback, media.EmbeddedAudio ? "音频包含在视频流中" : null, media.NoAudio ? "此视频无音轨" : null }.Where(s => !string.IsNullOrEmpty(s)))
        : "播放后可选择当前视频支持的画质与音质。";

    public bool IsImportOpen { get => _importOpen; set => Set(ref _importOpen, value); }
    public string ImportMode { get => _importMode; private set { if (Set(ref _importMode, value)) Changed(nameof(ImportVideo), nameof(ImportCollection), nameof(ImportIds), nameof(ImportLabel), nameof(ImportPlaceholder), nameof(ImportButtonText)); } }
    public bool ImportVideo => ImportMode == "video";
    public bool ImportCollection => ImportMode == "collection";
    public bool ImportIds => ImportMode == "ids";
    public string ImportLabel => ImportVideo ? "视频链接或 BV 号" : "合集或系列链接";
    public string ImportPlaceholder => ImportVideo ? "粘贴 B 站视频链接，或输入 BV 号" : "粘贴 space.bilibili.com 下的合集或系列链接";
    public string ImportButtonText => IsBusy ? "正在导入…" : ImportVideo ? "导入视频" : "导入合集";
    public string ImportInput { get => _importInput; set => Set(ref _importInput, value); }
    public string UploaderId { get => _uploaderId; set => Set(ref _uploaderId, value); }
    public string CollectionId { get => _collectionId; set => Set(ref _collectionId, value); }
    public string CollectionKindId { get => _collectionKind; set => Set(ref _collectionKind, value); }
    public string ImportError { get => _importError; private set => Set(ref _importError, value); }

    public void Navigate(string page)
    {
        if (page is not ("Workspace" or "Library" or "Queue" or "Settings")) return;
        _page = page;
        Changed(nameof(Page), nameof(IsWorkspace), nameof(IsLibrary), nameof(IsQueue), nameof(IsSettings), nameof(CanAddFromHeader), nameof(PageTitle), nameof(PageSubtitle));
    }

    internal void SetDisplays(IReadOnlyList<DisplayTarget> displays, bool startup)
    {
        Displays = displays.Select((display, index) => new DisplayChoice(display, index + 1)).ToArray(); Changed(nameof(Displays));
        SelectedDisplay = Displays.FirstOrDefault(d => d.Id == _preferences.DisplayId) ?? Displays.FirstOrDefault(d => d.Primary) ?? Displays.FirstOrDefault();
        AutoStart = startup;
    }
    internal void SetAccount(BilibiliAccount? account) => AccountText = account is null ? "未登录" : account.Name + (account.IsVip ? " · 大会员" : "");
    internal void SyncMcpPreferences(bool enabled, int port) => _preferences = _preferences with { McpEnabled = enabled, McpPort = port };
    internal void SetAgentWallpaperEnabled(bool enabled)
    {
        IsWallpaperEnabled = enabled;
        _preferences = _preferences with { WallpaperEnabled = enabled };
    }
    internal void SetDesktopStatus(string status) => DesktopStatus = status;
    internal void Report(string text) => Notice = text;

    private AsyncUiCommand Action(Func<object?, Task> action, Func<object?, bool>? enabled = null) =>
        new(p => RunAsync(() => action(p)), p => !IsBusy && (enabled?.Invoke(p) ?? true));

    internal async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Notice = "";
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var text = ex switch
            {
                ContentAccessException access => access.Kind switch
                {
                    ContentFailureKind.InvalidInput => "链接或 ID 格式不正确，请检查后重试。",
                    ContentFailureKind.AuthenticationRequired => "请先登录，或重新登录后重试。",
                    ContentFailureKind.Network => "网络暂时不可用，请检查连接后重试。",
                    ContentFailureKind.RateLimited => "请求过于频繁，请稍后重试。",
                    ContentFailureKind.Removed => "视频已失效或不可访问，来源和其他条目仍会保留。",
                    _ => "暂时无法读取此内容，请稍后重试。"
                },
                ArgumentException => ex.Message,
                _ => "操作未完成，请检查网络、文件权限或播放器设置后重试。"
            };
            if (IsImportOpen) ImportError = text; else Notice = text;
        }
        finally { IsBusy = false; Refresh(); }
    }

    private async Task ImportAsync()
    {
        ImportError = "";
        if (ImportVideo)
        {
            BilibiliInput.Parse(ImportInput);
            await _library.ImportVideoAsync(ImportInput.Trim());
        }
        else
        {
            CollectionSource source;
            if (ImportIds)
            {
                if (!long.TryParse(UploaderId, out var uid) || uid <= 0 || !long.TryParse(CollectionId, out var id) || id <= 0)
                    throw new ArgumentException("UP 主 UID 和合集 / 系列 ID 都需要填写正整数。");
                source = new(uid, id, CollectionKindId == "series" ? CollectionKind.Series : CollectionKind.Season);
            }
            else source = BilibiliCollectionInput.Parse(ImportInput);
            await _library.ImportCollectionAsync(source);
        }
        IsImportOpen = false; ImportInput = "";
        Navigate("Library"); Notice = _library.Snapshot.Notice;
    }

    private async Task PlayVideoAsync(VideoRow row)
    {
        var item = _snapshot.Queue.FirstOrDefault(q => q.Key == row.Key);
        if (item is null)
        {
            await _library.EnqueueAsync([row.Key]);
            item = _library.Snapshot.Queue.Last(q => q.Key == row.Key);
        }
        await _library.PlayAsync(item.Id);
    }
    private VideoRow[] SelectedVideos() => CatalogRows.Where(r => r.IsSelected).ToArray();
    private async Task MoveVideosAsync(int direction)
    {
        var rows = SelectedVideos();
        if (direction > 0) Array.Reverse(rows);
        foreach (var row in rows) await _library.MoveEntryAsync(row.Key, direction);
    }
    private async Task DeleteVideosAsync()
    {
        foreach (var row in SelectedVideos()) await _library.RemoveEntryAsync(row.Key);
    }
    private async Task CommitSeekAsync()
    {
        var position = Math.Clamp(SeekPosition, 0, DurationSeconds);
        IsSeeking = false;
        await _library.SeekAsync(TimeSpan.FromSeconds(position));
    }
    private async Task SaveSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(MpvPath)) throw new ArgumentException("请填写 mpv 播放器路径。");
        var preferences = _preferences with { WallpaperEnabled = IsWallpaperEnabled, ContinueAudioWhenHidden = ContinueAudio,
            DisplayId = SelectedDisplay?.Id, FillWindow = FillWindow, MpvPath = MpvPath.Trim() };
        await _saveSettings(preferences, AutoStart);
        _preferences = preferences;
        SettingsStatus = "设置已应用";
    }
    private async Task SaveQualityAsync()
    {
        var selection = new MediaSelectionPreferences(VideoQualityId.Length == 0 ? null : VideoQualityId, AudioQualityId.Length == 0 ? null : AudioQualityId);
        await _library.SetMediaSelectionAsync(selection);
        var preferences = _preferences with { PreferredVideoQualityId = selection.VideoQualityId, PreferredAudioQualityId = selection.AudioQualityId };
        await _saveSettings(preferences, AutoStart);
        _preferences = preferences;
        SettingsStatus = "画质与音质偏好已保存";
    }

    private void LibraryChanged(object? sender, EventArgs args)
    {
        if (_dispatcher.CheckAccess()) Refresh();
        else _dispatcher.BeginInvoke(Refresh);
    }
    internal void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _snapshot = _library.Snapshot;
            if (!IsSeeking) SeekPosition = _snapshot.Playback.PositionSeconds;
            if (!IsAdjustingVolume) Volume = _snapshot.Playback.Volume;
            SyncSources(); RefreshRows(); SyncQuality();
            Changed(nameof(HasEntries), nameof(IsEmpty), nameof(HasQueue), nameof(QueueEmpty), nameof(HasCurrent), nameof(IsPlaying), nameof(RecoveryPending),
                nameof(QueueCount), nameof(EntryCount), nameof(SourceCount), nameof(SourcesSummary), nameof(PageSubtitle), nameof(CurrentTitle), nameof(CurrentUploader),
                nameof(CurrentSource), nameof(PlaybackStatus), nameof(PlayButtonLabel), nameof(PositionText), nameof(DurationText), nameof(DurationSeconds), nameof(ModeId), nameof(ModeDescription), nameof(QualityStatus));
            CommandManager.InvalidateRequerySuggested();
        }
        finally { _refreshing = false; }
    }

    private string FindSource(ContentKey key) => _snapshot.Sources.FirstOrDefault(s => s.Members.Contains(key))?.Title ?? "本地片库";
    private void SyncSources()
    {
        var rows = _snapshot.Sources.Select(source => new SourceRow(source.Id, source.Title,
            _snapshot.Entries.FirstOrDefault(e => source.Members.Contains(e.Key))?.Uploader ?? "未知 UP 主", source.Members.Count,
            source.Problems.Count > 0 ? $"{source.Problems.Count} 个视频不可访问" : (source.Collection?.Kind == CollectionKind.Series ? "系列" : source.Collection is null ? "视频" : "合集") + " · " + (source.RefreshedAt.LocalDateTime.Date == DateTime.Today ? "今天已刷新" : source.RefreshedAt.ToLocalTime().ToString("MM-dd 已刷新")), source.Members)).ToArray();
        Sync(SourceRows, rows);
        Sync(SourceOptions, new[] { new SelectOption("", "全部来源") }.Concat(rows.Select(r => new SelectOption(r.Id, r.Title))).ToArray());
        if (SourceFilter.Length > 0 && !_snapshot.Sources.Any(s => s.Id == SourceFilter)) SourceFilter = "";
    }
    private bool Matches(string title, string uploader, string bvid) => string.IsNullOrWhiteSpace(SearchText) ||
        (title + " " + uploader + " " + bvid).Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    private void RefreshRows()
    {
        var wanted = new List<VideoRow>();
        var source = _snapshot.Sources.FirstOrDefault(s => s.Id == SourceFilter);
        for (var i = 0; i < _snapshot.Entries.Count; i++)
        {
            var entry = _snapshot.Entries[i];
            if (!_videos.TryGetValue(entry.Key, out var row)) _videos[entry.Key] = row = new(entry, SelectionChanged);
            row.Update(entry, i + 1, FindSource(entry.Key), _snapshot);
            if (!Matches(entry.Title, entry.Uploader, entry.Key.Bvid) || source is not null && !source.Members.Contains(entry.Key) ||
                StatusFilter == "failed" && !row.IsFailed || StatusFilter == "ready" && row.IsFailed) continue;
            wanted.Add(row);
        }
        foreach (var key in _videos.Keys.Where(k => !_snapshot.Entries.Any(e => e.Key == k)).ToArray()) _videos.Remove(key);
        Sync(CatalogRows, wanted);
        var queue = new List<QueueRow>(); var upcoming = new List<QueueRow>();
        var currentIndex = _snapshot.Queue.ToList().FindIndex(q => q.Id == _snapshot.Playback.CurrentId);
        for (var i = 0; i < _snapshot.Queue.Count; i++)
        {
            var item = _snapshot.Queue[i]; var entry = _snapshot.Entries.FirstOrDefault(e => e.Key == item.Key);
            if (!_queue.TryGetValue(item.Id, out var row)) _queue[item.Id] = row = new(item);
            row.Update(item, entry, i + 1, FindSource(item.Key), _snapshot);
            if (Matches(row.Title, row.Uploader, row.Key.Bvid)) queue.Add(row);
            if (i > currentIndex && !row.IsCurrent && upcoming.Count < 3) upcoming.Add(row);
        }
        foreach (var id in _queue.Keys.Where(id => !_snapshot.Queue.Any(q => q.Id == id)).ToArray()) _queue.Remove(id);
        Sync(QueueRows, queue); Sync(UpcomingRows, upcoming);
        if (SelectedQueue is { } selected && !QueueRows.Contains(selected)) SelectedQueue = null;
        SelectionChanged(); Changed(nameof(NoFilterResults), nameof(NoQueueResults));
    }
    private void SyncQuality()
    {
        // Replacing a temporary "unavailable preference" option may briefly clear SelectedValue.
        var video = _videoId; var audio = _audioId;
        Sync(VideoOptions, QualityOptions(_snapshot.Media?.VideoOptions, _preferences.PreferredVideoQualityId));
        Sync(AudioOptions, QualityOptions(_snapshot.Media?.AudioOptions, _preferences.PreferredAudioQualityId));
        _videoId = video; _audioId = audio;
        Changed(nameof(VideoQualityId), nameof(AudioQualityId));
    }
    private static SelectOption[] QualityOptions(IReadOnlyList<MediaQualityOption>? options, string? preference)
    {
        var result = new List<SelectOption> { new("", "自动 · 最高可用") };
        if (options is not null) result.AddRange(options.Select(o => new SelectOption(o.Id, o.Label)));
        if (preference is { Length: > 0 } && !result.Any(o => o.Id == preference)) result.Add(new(preference, "偏好 " + preference + " · 当前不可用"));
        return result.ToArray();
    }
    private void SelectionChanged()
    {
        Changed(nameof(SelectedCount), nameof(SelectionText), nameof(HasSelection), nameof(AllVideosSelected));
        CommandManager.InvalidateRequerySuggested();
    }
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], wanted[i])) continue;
            var old = target.IndexOf(wanted[i]);
            if (old >= 0) target.Move(old, i); else target.Insert(i, wanted[i]);
        }
        while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
    }
    public void Dispose() => _library.Changed -= LibraryChanged;
}
