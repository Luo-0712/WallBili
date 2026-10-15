using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using WallBili.Player.Mpv;

namespace WallBili.Desktop;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly BilibiliClient _bilibili;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MpvPlayer? _player;
    private string? _playerPath;
    private ContentKey? _activeContent;
    private TimeSpan _position;
    private TimeSpan _duration;
    private bool _busy;
    private bool _polling;
    private bool _canControl;
    private bool _paused;
    private bool _closing;
    private bool _closed;
    private Task _pendingOperation = Task.CompletedTask;
    private int _playbackVersion;
    private bool _loadingMedia;
    private (int Version, PlaybackEndedEventArgs Event)? _pendingPlaybackEnd;

    public MainWindow()
    {
        InitializeComponent();
        _bilibili = new BilibiliClient(_http);
        MpvPathInput.Text = Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv";
        _positionTimer.Tick += PositionTimer_Tick;
        _positionTimer.Start();
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e) => await FetchVideoAsync();

    private async void VideoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy)
        {
            e.Handled = true;
            await FetchVideoAsync();
        }
    }

    private async Task FetchVideoAsync()
    {
        var input = VideoInput.Text.Trim();
        if (input.Length == 0)
        {
            SetStatus("请输入视频链接或 BV 号。", true);
            return;
        }

        await RunOperationAsync("正在获取视频信息…", async token =>
        {
            var requestedPage = BilibiliInput.Parse(input).Page;
            var video = await _bilibili.GetVideoAsync(input, token);
            VideoTitle.Text = video.Title;
            VideoMetadata.Text = $"UP 主：{video.Uploader} · {video.Bvid} · 共 {video.Parts.Count} P · {FormatTime(video.Duration)}";
            PartsBox.ItemsSource = video.Parts;
            PartsBox.SelectedItem = video.Parts.FirstOrDefault(part => part.Page == requestedPage) ?? video.Parts.FirstOrDefault();
            SetStatus(video.Parts.Count > 0 ? "视频信息已获取，请选择分 P 后播放。" : "该视频没有可播放的分 P。", video.Parts.Count == 0);
        });
    }

    private void PartsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPartText is not null)
        {
            SelectedPartText.Text = PartsBox.SelectedItem is VideoPart part
                ? $"所选分 P：{part.Key.Bvid} · CID {part.Key.Cid} · {FormatTime(part.Duration)}"
                : string.Empty;
        }
        UpdateControls();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (PartsBox.SelectedItem is not VideoPart part)
        {
            return;
        }

        await RunOperationAsync("正在解析音视频并启动播放器…", async token =>
        {
            var session = await _bilibili.ResolveMediaAsync(part.Key, token);
            var player = await EnsurePlayerAsync(token);
            _canControl = false;
            _playbackVersion++;
            _activeContent = part.Key;
            _position = TimeSpan.Zero;
            _duration = part.Duration;
            await LoadMediaAsync(player, session, token);
            await player.ResumeAsync(token);
            _paused = false;
            _canControl = true;
            UpdatePosition();
            ShowSession(session);
            SetStatus($"正在播放 P{part.Page} · {part.Title}");
        });
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在暂停…", async token =>
        {
            await _player!.PauseAsync(token);
            _paused = true;
            SetStatus("已暂停。");
        });
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在继续播放…", async token =>
        {
            await _player!.ResumeAsync(token);
            _paused = false;
            SetStatus("正在播放。");
        });
    }

    private async void Seek_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SeekInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds ||
            (_duration > TimeSpan.Zero && seconds > _duration.TotalSeconds))
        {
            SetStatus("请输入有效的跳转秒数，且不要超过视频时长。", true);
            return;
        }

        await RunOperationAsync("正在跳转…", async token =>
        {
            await _player!.SeekAsync(TimeSpan.FromSeconds(seconds), token);
            _position = TimeSpan.FromSeconds(seconds);
            UpdatePosition();
            SetStatus($"已跳转至 {FormatTime(_position)}。");
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_activeContent is not { } content)
        {
            return;
        }

        await RunOperationAsync("正在重新解析播放地址…", async token =>
        {
            var position = _position;
            var paused = _paused;
            var session = await _bilibili.ResolveMediaAsync(content, token);
            if (_canControl && _player is not null)
            {
                try
                {
                    var seconds = await _player.GetPropertyAsync<double?>("time-pos", token);
                    if (seconds is { } value && double.IsFinite(value) && value >= 0)
                    {
                        position = TimeSpan.FromSeconds(value);
                    }
                }
                catch (MpvException)
                {
                    // A failed/expired stream can lose IPC time-pos; retain the last observed position.
                }
            }

            var player = await EnsurePlayerAsync(token);
            _canControl = false;
            _playbackVersion++;
            await LoadMediaAsync(player, session, token);
            if (position > TimeSpan.Zero)
            {
                await player.SeekAsync(position, token);
            }

            if (paused)
            {
                await player.PauseAsync(token);
            }
            else
            {
                await player.ResumeAsync(token);
            }

            _position = position;
            _paused = paused;
            _canControl = true;
            UpdatePosition();
            ShowSession(session);
            SetStatus($"已重新解析并恢复至 {FormatTime(position)}{(paused ? "（已暂停）" : "。")}");
        });
    }

    private async Task<MpvPlayer> EnsurePlayerAsync(CancellationToken token)
    {
        var path = MpvPathInput.Text.Trim();
        if (path.Length == 0)
        {
            path = "mpv";
        }

        if (_player is not null && !string.Equals(_playerPath, path, StringComparison.Ordinal))
        {
            await DisposePlayerAsync();
        }

        if (_player is null)
        {
            var player = new MpvPlayer(new MpvPlayerOptions { ExecutablePath = path });
            player.PlaybackEnded += Player_PlaybackEnded;
            _player = player;
            _playerPath = path;
        }

        try
        {
            await _player.StartAsync(token);
            return _player;
        }
        catch
        {
            await DisposePlayerAsync();
            throw;
        }
    }

    private async Task LoadMediaAsync(MpvPlayer player, MediaSession session, CancellationToken token)
    {
        _pendingPlaybackEnd = null;
        _loadingMedia = true;
        try
        {
            await player.LoadAsync(session, token);
        }
        finally
        {
            _loadingMedia = false;
        }
    }

    private void Player_PlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_closing || !ReferenceEquals(sender, _player) || _loadingMedia)
            {
                return;
            }

            if (_busy)
            {
                _pendingPlaybackEnd = (_playbackVersion, e);
                return;
            }

            ApplyPlaybackEnd(e);
        });
    }

    private void ApplyPlaybackEnd(PlaybackEndedEventArgs e)
    {
        _canControl = false;
        SetStatus(e.IsError ? "播放失败。可尝试重新解析并恢复进度。" : "播放已结束，可重新播放或重新解析。", e.IsError);
        UpdateControls();
    }

    private async void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || _busy || !_canControl || _player is null || _closing)
        {
            return;
        }

        _polling = true;
        var player = _player;
        var version = _playbackVersion;
        try
        {
            var seconds = await player.GetPropertyAsync<double?>("time-pos", _lifetime.Token);
            if (!_busy && !_closing && version == _playbackVersion && ReferenceEquals(player, _player) &&
                seconds is { } value && double.IsFinite(value) && value >= 0)
            {
                _position = TimeSpan.FromSeconds(value);
                UpdatePosition();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_closing && !_busy && version == _playbackVersion && ReferenceEquals(player, _player))
            {
                _canControl = false;
                SetStatus(DescribeError(ex), true);
                UpdateControls();
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> operation)
    {
        if (_busy || _closing)
        {
            return;
        }

        _busy = true;
        UpdateControls();
        SetStatus(message);
        try
        {
            _pendingOperation = operation(_lifetime.Token);
            await _pendingOperation;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetStatus(DescribeError(ex), true);
        }
        finally
        {
            _busy = false;
            UpdateControls();
            if (!_closing && _pendingPlaybackEnd is { } ended)
            {
                _pendingPlaybackEnd = null;
                if (ended.Version == _playbackVersion)
                {
                    ApplyPlaybackEnd(ended.Event);
                }
            }
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        ContentAccessException content => content.Kind switch
        {
            ContentFailureKind.InvalidInput => "输入无效，请填写 bilibili 视频链接或 BV 号。",
            ContentFailureKind.Removed => "视频已失效或已被删除。",
            ContentFailureKind.AuthenticationRequired => "视频需要登录或额外权限，请选择可公开访问的视频。",
            ContentFailureKind.RateLimited => "请求过于频繁，请稍后重试。",
            ContentFailureKind.Network => "网络请求失败，请检查连接后重试。",
            ContentFailureKind.Unsupported => "当前内容或媒体格式暂不支持。",
            ContentFailureKind.Inaccessible => "当前无法访问该视频，请检查视频是否公开可用。",
            _ => "视频服务返回了无法识别的数据，请稍后重试。"
        },
        MpvException mpv => $"播放器错误（{mpv.Code}）：{mpv.Message}",
        HttpRequestException => "网络请求失败，请检查网络连接后重试。",
        OperationCanceledException => "请求超时，请重试。",
        ArgumentException => "输入无效，请检查视频链接或 BV 号。",
        _ => "操作失败，请检查视频是否可访问以及 mpv 配置后重试。"
    };

    private void ShowSession(MediaSession session)
    {
        var expiry = session.ExpiresAt is { } expires ? $" · 到期时间 {expires.ToLocalTime():HH:mm:ss}" : string.Empty;
        SessionText.Text = $"当前播放：{session.Content.Bvid} · CID {session.Content.Cid}\n" +
                           $"视频：{session.Video.Codec} · 清晰度 {session.Video.Quality}" +
                           $" · 音频：{session.Audio?.Codec ?? "无独立音轨"}" + expiry;
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkSlateGray;
    }

    private void UpdateControls()
    {
        if (FetchButton is null)
        {
            return;
        }

        var available = !_busy && !_closing;
        FetchButton.IsEnabled = available;
        VideoInput.IsEnabled = available;
        MpvPathInput.IsEnabled = available;
        PartsBox.IsEnabled = available && PartsBox.HasItems;
        PlayButton.IsEnabled = available && PartsBox.SelectedItem is VideoPart;
        PauseButton.IsEnabled = available && _canControl && !_paused;
        ResumeButton.IsEnabled = available && _canControl && _paused;
        RefreshButton.IsEnabled = available && _activeContent is not null;
        SeekInput.IsEnabled = available && _canControl;
        SeekButton.IsEnabled = available && _canControl;
    }

    private void UpdatePosition()
    {
        PositionText.Text = $"{FormatTime(_position)} / {FormatTime(_duration)}";
        PlaybackProgress.Maximum = Math.Max(_duration.TotalSeconds, 1);
        PlaybackProgress.Value = Math.Clamp(_position.TotalSeconds, 0, PlaybackProgress.Maximum);
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    private async Task DisposePlayerAsync()
    {
        var player = _player;
        _player = null;
        _playerPath = null;
        _canControl = false;
        _playbackVersion++;
        _pendingPlaybackEnd = null;
        if (player is not null)
        {
            player.PlaybackEnded -= Player_PlaybackEnded;
            await player.DisposeAsync();
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _positionTimer.Stop();
        _lifetime.Cancel();
        UpdateControls();
        try
        {
            try
            {
                await _pendingOperation;
            }
            catch (Exception)
            {
                // The operation reports its own safe error; closing still waits for cleanup.
            }
            await DisposePlayerAsync();
        }
        catch (Exception)
        {
            // MpvPlayer owns process cleanup, including forced termination in DisposeAsync.
        }
        finally
        {
            _http.Dispose();
            _lifetime.Dispose();
            _closed = true;
            Close();
        }
    }
}
