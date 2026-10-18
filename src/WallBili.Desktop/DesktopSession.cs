using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Win32;
using WallBili.Core;
using WallBili.Desktop.Presentation;
using WallBili.Infrastructure.Bilibili;
using WallBili.Infrastructure.Storage;
using WallBili.Player.Mpv;

namespace WallBili.Desktop;

/// <summary>Owns playback services independently of the new view's layout and navigation.</summary>
internal sealed class DesktopSession : IAsyncDisposable
{
    private readonly StartupOptions _options;
    private readonly HttpClient? _http;
    private readonly AuthStore? _auth;
    private readonly DesktopVideoHost? _desktop;
    private readonly StartupRegistration _startup;
    private readonly DispatcherTimer _timer;
    private DesktopPreferences _preferences;
    private bool _ticking, _disposed;
    public BilibiliClient? Client { get; }
    public LibrarySession Library { get; }
    public ShellViewModel ViewModel { get; }

    internal DesktopSession(StartupOptions options)
    {
        _options = options;
        _preferences = options.Preview ? new() { MpvPath = @"D:\tools\mpv\mpv.exe" } : DesktopPreferences.Read(Path.Combine(options.DataDirectory, "preferences.json"));
        _startup = new(options);
        if (options.Preview)
            Library = new(new PreviewSession.Content(), new PreviewSession.Store(options.EmptyPreview ? new() : PreviewSession.Seed()), new PreviewSession.Player());
        else
        {
            _http = new() { Timeout = TimeSpan.FromSeconds(35) };
            Client = new(_http);
            _auth = new(Path.Combine(options.DataDirectory, "auth.dat"));
            _desktop = new();
            var player = new MpvQueuePlayer(() => new()
            {
                ExecutablePath = _preferences.MpvPath, WindowId = _desktop.Handle.ToInt64(), FillWindow = _preferences.FillWindow
            });
            Library = new(Client, new SqliteLibraryStore(Path.Combine(options.DataDirectory, "library.db")), player,
                selection: new(_preferences.PreferredVideoQualityId, _preferences.PreferredAudioQualityId));
        }
        ViewModel = new(Library, _preferences, SaveSettingsAsync, options.Preview);
        ViewModel.SetDisplays(options.Preview ? [new("preview-display", 0, 0, 2560, 1440, true)] : DesktopVideoHost.GetDisplays(), !options.Preview && _startup.IsEnabled());
        _timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, Tick, Dispatcher.CurrentDispatcher);
        _timer.Stop();
        if (Client is not null) Client.AccountChanged += AccountChanged;
    }

    public async Task InitializeAsync()
    {
        await ViewModel.RunAsync(async () =>
        {
            if (_auth?.Load() is { } cookies && Client is not null)
                try { await Client.ImportCookiesAsync(cookies); }
                catch (ContentAccessException ex) when (ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited) { }
                catch (ContentAccessException) { _auth.Clear(); }
            await Library.InitializeAsync();
            ViewModel.SetAccount(Client?.CurrentAccount());
            RefreshDesktop();
        });
        if (!Library.IsInitialized) return;
        _timer.Start();
        if (!_options.Preview)
        {
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        }
    }
    private async void Tick(object? sender, EventArgs e)
    {
        if (_ticking || _disposed || ViewModel.IsBusy || !Library.IsInitialized) return;
        _ticking = true;
        try
        {
            var recreated = RefreshDesktop();
            if (recreated) await Library.RebindOutputAsync();
            await Library.CheckpointAsync();
            await Library.RetryRecoveryAsync();
        }
        catch (Exception) { ViewModel.Report("暂时无法同步播放状态，队列与进度会保留。请检查播放器或重试。"); }
        finally { _ticking = false; }
    }
    private bool RefreshDesktop()
    {
        if (_desktop is null)
        {
            ViewModel.SetDesktopStatus(_preferences.WallpaperEnabled ? "显示效果示意 · 预览不会更改桌面" : "画面已隐藏，音乐继续播放");
            return false;
        }
        var changed = _desktop.Refresh(_preferences.WallpaperEnabled, _preferences.DisplayId, Library.Snapshot.OutputActive);
        ViewModel.SetDesktopStatus(_desktop.Status);
        return changed;
    }
    private async Task SaveSettingsAsync(DesktopPreferences preferences, bool startup)
    {
        var previous = _preferences;
        if (!_options.Preview)
        {
            _startup.SetEnabled(startup);
            preferences.Save(Path.Combine(_options.DataDirectory, "preferences.json"));
        }
        _preferences = preferences;
        await Library.SetVideoFitAsync(preferences.FillWindow);
        var recreated = RefreshDesktop();
        if (recreated || previous.MpvPath != preferences.MpvPath) await Library.RebindOutputAsync();
        if (previous.WallpaperEnabled && !preferences.WallpaperEnabled && !preferences.ContinueAudioWhenHidden && Library.Snapshot.Playback.Intent == PlaybackIntent.Playing)
            await Library.PauseAsync();
    }
    internal void SaveLogin(string cookies) { _auth?.Save(cookies); ViewModel.SetAccount(Client?.CurrentAccount()); }
    internal async Task SignOutAsync()
    {
        if (Client is null) return;
        await Client.SignOutAsync(); _auth?.Clear();
        ViewModel.SetAccount(null);
        ViewModel.Report("已退出登录，片库和队列仍保留在本机。");
    }
    private void AccountChanged(object? sender, BilibiliAccountChangedEventArgs e) =>
        _timer.Dispatcher.BeginInvoke(() => { if (!_disposed) ViewModel.SetAccount(e.Account); });
    private void PowerChanged(object? sender, PowerModeChangedEventArgs e) => _timer.Dispatcher.BeginInvoke(async () =>
    {
        if (_disposed) return;
        if (e.Mode == PowerModes.Suspend) await ViewModel.RunAsync(() => Library.SuspendAsync());
        else if (e.Mode == PowerModes.Resume) await ViewModel.RunAsync(() => Library.ResumeAsync());
    });
    private void DisplaysChanged(object? sender, EventArgs e) => _timer.Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed) ViewModel.SetDisplays(DesktopVideoHost.GetDisplays(), _startup.IsEnabled());
    });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (!_options.Preview) { SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplaysChanged; }
        if (Client is not null) Client.AccountChanged -= AccountChanged;
        ViewModel.Dispose();
        try { await Library.DisposeAsync(); }
        finally { _desktop?.Dispose(); _http?.Dispose(); }
    }
}

internal sealed class StartupRegistration(StartupOptions options)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string Name => options.IsIsolated ? "WallBili." + options.InstanceId : "WallBili";
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(Name) as string, Command(), StringComparison.Ordinal);
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command(), RegistryValueKind.String);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
    internal string Command()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var dll = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $" \"{typeof(App).Assembly.Location}\"" : "";
        return $"\"{executable}\"{dll} --data-dir \"{options.DataDirectory}\" --background";
    }
}
