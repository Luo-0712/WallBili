using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class DesktopRegressionTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("64", null)]
    [InlineData(null, "30216")]
    public Task ManualSelectionReachesPlaybackAndThePreferenceFile(string? video, string? audio) => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        await f.Library.ImportVideoAsync("BV1GJ411x7h7", Token);
        await f.Library.EnqueueAsync(f.Library.Snapshot.Entries.Select(e => e.Key), Token);
        await f.Library.PlayAsync(token: Token);
        Set(f.Window, "_ready", true);
        Select(f.Window.VideoQualityBox, video); Select(f.Window.AudioQualityBox, audio);

        await (Task)Call(f.Window, "ApplyQualityPreferenceAsync")!;

        var saved = DesktopPreferences.Read(f.PreferencesPath);
        Assert.Equal(video, saved.PreferredVideoQualityId);
        Assert.Equal(audio, saved.PreferredAudioQualityId);
        Assert.Equal(new(video, audio), f.Content.LastSelection);
        Assert.DoesNotContain("操作未完成", f.Window.StatusText.Text);
    });

    [Fact]
    public Task StoredSelectionDoesNotReadTheCurrentControls() => OnSta(() =>
    {
        using var f = new WindowFixture();
        var preferences = new DesktopPreferences { PreferredVideoQualityId = "64", PreferredAudioQualityId = "30216" };
        Select(f.Window.VideoQualityBox, "120"); Select(f.Window.AudioQualityBox, "30280");

        var selection = (MediaSelectionPreferences)Call(f.Window, "ToSelection", preferences)!;

        Assert.Equal(new("64", "30216"), selection);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SelectingAutomaticClearsThePreviouslySavedTier() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        Set(f.Window, "_ready", true);
        Set(f.Window, "_preferences", new DesktopPreferences { MpvPath = "mpv", WallpaperEnabled = false,
            PreferredVideoQualityId = "64", PreferredAudioQualityId = "30216" });
        Select(f.Window.VideoQualityBox, null); Select(f.Window.AudioQualityBox, null);

        await (Task)Call(f.Window, "ApplyQualityPreferenceAsync")!;

        var saved = DesktopPreferences.Read(f.PreferencesPath);
        Assert.Null(saved.PreferredVideoQualityId);
        Assert.Null(saved.PreferredAudioQualityId);
        Assert.DoesNotContain("操作未完成", f.Window.StatusText.Text);
    });

    [Theory]
    [InlineData(ContentFailureKind.Network)]
    [InlineData(ContentFailureKind.RateLimited)]
    public Task StartupLoginFailureStillInitializesTheLibrary(ContentFailureKind kind) => OnSta(async () =>
    {
        using var f = new WindowFixture(kind);
        f.Auth.Save("SESSDATA=stored-token");
        Call(f.Window, "Window_Loaded", f.Window, new RoutedEventArgs());
        // Let the window's async-void Loaded handler finish on its dispatcher.
        while ((bool)Get(f.Window, "_busy")!) await Task.Delay(10, Token);

        Assert.True(f.Library.IsInitialized);
        Assert.True(f.Window.PlayerPanel.IsEnabled);
        Assert.NotNull(f.Auth.Load());
        Assert.Contains("重试", f.Window.AccountText.Text);
    });

    [Fact]
    public Task NetworkRecoveryRetriesTheStoredLoginWithoutRestarting() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        f.Auth.Save("SESSDATA=stored-token");
        Call(f.Window, "Window_Loaded", f.Window, new RoutedEventArgs());
        while ((bool)Get(f.Window, "_busy")!) await Task.Delay(10, Token);
        Assert.True(f.Library.IsInitialized);
        f.AccountResponse = () => Json("""{"code":0,"data":{"isLogin":true,"mid":42,"uname":"Uploader"}}""");
        Set(f.Window, "_networkChanged", true);

        await (Task)Call(f.Window, "MaintainAsync")!;

        Assert.Equal(42, ((BilibiliClient)Get(f.Window, "_client")!).CurrentAccount()!.Uid);
        Assert.False((bool)Get(f.Window, "_accountRestorePending")!);
        Assert.Contains("Uploader", f.Window.AccountText.Text);
        Assert.True(f.Window.PlayerPanel.IsEnabled);
    });

    [Fact]
    public Task FailedAccountSynchronizationCanBeRetriedForTheSameAccount() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        Set(f.Window, "_hasBoundRoamingAccount", true);
        Set(f.Window, "_boundRoamingAccount", null);
        Set(f.Window, "_roamingAccountTask", Task.FromException(new IOException("temporary store error")));

        await (Task)Call(f.Window, "SynchronizeRoamingAccountAsync", Token)!;

        Assert.Null(f.Library.Snapshot.Roaming?.AccountId);
    });

    [Fact]
    public Task BackgroundRoamingRefreshAllowsPausingAndStoppingThePlayer() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        await f.Library.ImportVideoAsync("BV1GJ411x7h7", Token);
        await f.Library.EnqueueAsync(f.Library.Snapshot.Entries.Select(e => e.Key), Token);
        await f.Library.PlayAsync(token: Token);
        Set(f.Window, "_ready", true);
        Set(f.Window, "_startingRoaming", true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> request = _ => release.Task;
        var pending = (Task)Call(f.Window, "RunRoamingAsync", request, true)!;
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.False((bool)Get(f.Window, "_busy")!);
            Assert.True(f.Window.PlayerPanel.IsEnabled);
            Assert.True(f.Window.RoamingPauseButton.IsEnabled);
            Assert.True(f.Window.RoamingStopButton.IsEnabled);

            Call(f.Window, "Pause_Click", f.Window, new RoutedEventArgs());
            while ((bool)Get(f.Window, "_busy")!) await Task.Delay(10, Token);
            Assert.Equal(PlaybackIntent.Paused, f.Library.Snapshot.Playback.Intent);

            Call(f.Window, "Stop_Click", f.Window, new RoutedEventArgs());
            while ((bool)Get(f.Window, "_busy")!) await Task.Delay(10, Token);
            Assert.Equal(PlaybackIntent.Stopped, f.Library.Snapshot.Playback.Intent);
        }
        finally { release.TrySetResult(); await pending; }
    });

    [Fact]
    public Task RoamingRenderingShowsTheManualQueueInsteadOfItsInternalTransport() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        Set(f.Window, "_ready", true);
        var manual = new CatalogEntry(new("BV1GJ411x7h7", 101), "Manual saved", "Uploader", 1, TimeSpan.FromMinutes(1));
        var roaming = new CatalogEntry(new("BV1xx411c7mD", 102), "Roaming transport", "Uploader", 1, TimeSpan.FromMinutes(1));
        var manualId = Guid.NewGuid(); var roamingId = Guid.NewGuid();
        Set(f.Library, "_snapshot", new LibrarySnapshot([manual, roaming], [], [new(roamingId, roaming.Key)],
            new(roamingId, Intent: PlaybackIntent.Playing), "Playing", Source: PlaybackSource.Roaming,
            Roaming: RoamingSnapshot.Empty with { AccountId = 42 }, ManualQueue: [new(manualId, manual.Key)],
            ManualPlayback: new(manualId, PositionSeconds: 12)));

        Call(f.Window, "Render");

        var row = Assert.Single(f.Window.QueueGrid.Items.Cast<object>());
        Assert.Equal("Manual saved", row.GetType().GetProperty("Title")!.GetValue(row));
        Assert.Contains("Roaming transport", f.Window.CurrentText.Text);
        Assert.Equal("订阅漫游", f.Window.PlaybackSourceText.Text);
        Assert.False(f.Window.QueueUpButton.IsEnabled);
        Assert.False(f.Window.RemoveQueueButton.IsEnabled);
    });

    [Fact]
    public Task RefreshingPlaybackDoesNotDiscardAnUnsubmittedScheduleEdit() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        Set(f.Window, "_ready", true);
        Set(f.Library, "_snapshot", f.Library.Snapshot with { Roaming = RoamingSnapshot.Empty with { AccountId = 42 } });
        Call(f.Window, "RenderRoaming");
        f.Window.RoamingScheduleBox.IsChecked = true;
        f.Window.RoamingScheduleInput.Text = "19:30";

        Call(f.Window, "RenderRoaming");

        Assert.True(f.Window.RoamingScheduleBox.IsChecked);
        Assert.Equal("19:30", f.Window.RoamingScheduleInput.Text);
    });

    [Fact(Explicit = true)]
    public Task VisibleRoamingControlsSupportAutomationAndRenderAScreenshot() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        await f.Library.InitializeAsync(Token);
        var creator = new FollowedCreator(900001, "[测试] 示例 UP 主");
        var now = DateTimeOffset.Now;
        CreatorVideo[] videos = [
            new("BV_TEST_FRESH", creator.Uid, creator.Name, "[测试] 今日新作 · 城市夜景", now.AddHours(-2), TimeSpan.FromMinutes(12)),
            new("BV_TEST_OLD", creator.Uid, creator.Name, "[测试] 未播旧作 · 雨声与窗景", now.AddDays(-20), TimeSpan.FromMinutes(30)),
            new("BV_TEST_SEEN", creator.Uid, creator.Name, "[测试] 已播内容 · 海边日出", now.AddDays(-40), TimeSpan.FromMinutes(8))
        ];
        await ((RoamingService)Get(f.Window, "_roaming")!).DisposeAsync();
        var roaming = new RoamingService(f.Library, new VisibleDiscovery(creator, videos));
        Set(f.Window, "_roaming", roaming);
        await roaming.SetAccountAsync(900001, Token);
        await roaming.SaveSubscriptionsAsync([creator], Token);
        await roaming.RefreshAsync(Token);
        var state = (LibraryState)Get(f.Library, "_state")!;
        state.RoamingAccounts[creator.Uid].Videos[2] = state.RoamingAccounts[creator.Uid].Videos[2] with
            { CompletedAt = now.AddDays(-2), LastPlayedAt = now.AddDays(-2) };
        await f.Library.ImportVideoAsync("BV_TEST_MANUAL", Token);
        state.Entries[0] = state.Entries[0] with { Title = "[测试] 手动视频 · 等待播完后切入" };
        await f.Library.EnqueueAsync(state.Entries.Select(e => e.Key), Token);
        await f.Library.PlayAsync(token: Token);

        // Render the actual production XAML in an ordinary Window. All control events are connected
        // to the real fixture methods; the original Window Loaded/Closing lifecycle never runs.
        var host = LoadVisibleFixture(f.Window);
        try
        {
            Set(f.Window, "_ready", true);
            f.Window.AccountText.Text = "[测试] 模拟账号 · 不使用真实凭据";
            f.Window.MpvPathInput.Text = "[测试] 隔离的模拟播放器";
            Call(f.Window, "Render");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Loaded += (_, _) => loaded.TrySetResult();
            host.Show();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await WaitForLayoutAsync(host, () => host.IsVisible && host.IsLoaded);
            var handle = new WindowInteropHelper(host).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(IsWindowVisible(handle));

            // 页签顺序由界面决定，这里按承载的面板找到对应的页，而不是写死下标。
            var tabs = f.Window.ContentTabs;
            var manualTab = await SelectTabContainingAsync(host, tabs, f.Window.ListsPanel);
            var roamingTab = await SelectTabContainingAsync(host, tabs, f.Window.RoamingPanel);
            SelectTab(roamingTab);
            await WaitForLayoutAsync(host, () => roamingTab.IsSelected && f.Window.RoamingPanel.IsVisible);
            SelectTab(manualTab);
            await WaitForLayoutAsync(host, () => manualTab.IsSelected && f.Window.ListsPanel.IsVisible);
            SelectTab(roamingTab);
            await WaitForLayoutAsync(host, () => roamingTab.IsSelected && f.Window.RoamingPanel.IsVisible);

            var timePeer = new TextBoxAutomationPeer(f.Window.RoamingScheduleInput);
            ((IValueProvider)timePeer.GetPattern(PatternInterface.Value)!).SetValue("21:15");
            var togglePeer = new CheckBoxAutomationPeer(f.Window.RoamingScheduleBox);
            ((IToggleProvider)togglePeer.GetPattern(PatternInterface.Toggle)!).Toggle();
            InvokeButton(f.Window.RoamingScheduleSaveButton);
            await WaitForLayoutAsync(host, () => f.Library.Snapshot.Roaming is { ScheduleEnabled: true } schedule &&
                schedule.ScheduleTime == new TimeSpan(21, 15, 0));
            Assert.Contains("21:15", f.Window.RoamingNextScheduleText.Text);

            var intent = await f.Library.TryBeginScheduledRoamingAsync(creator.Uid, DateOnly.FromDateTime(DateTime.Now),
                new TimeSpan(21, 15, 0), f.Library.RoamingScheduleRevision, Token);
            Assert.NotNull(intent);
            await f.Library.CompleteScheduledRoamingAsync(intent.Value, Token);
            Call(f.Window, "Render");
            await WaitForLayoutAsync(host, () => f.Window.RoamingPendingPanel.IsVisible && f.Window.RoamingGrid.Items.Count == 3);
            Assert.True(f.Window.RoamingPauseButton.IsEnabled);
            Assert.True(f.Window.RoamingStopButton.IsEnabled);

            var artifactDirectory = Environment.GetEnvironmentVariable("WALLBILI_UI_ARTIFACT_DIR") ??
                @"C:\Users\22369\Desktop\codex-work\WallBili";
            Directory.CreateDirectory(artifactDirectory);
            var screenshot = Path.Combine(artifactDirectory, "subscription-roaming.png");
            var root = (Grid)host.Content;
            var dpi = VisualTreeHelper.GetDpi(root);
            var width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
            var height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
            var frame = new DrawingVisual();
            using (var drawing = frame.RenderOpen())
            {
                drawing.DrawRectangle(host.Background, null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(root), null,
                    new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX),
                (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(screenshot)) encoder.Save(output);
            Assert.True(new FileInfo(screenshot).Length > 1024);
            TestContext.Current.TestOutputHelper?.WriteLine($"NativeVisible={IsWindowVisible(handle)}; HWND={handle}; DPI={dpi.DpiScaleX:0.##}; Screenshot={screenshot}");

            // Invoke a real production action through WPF's automation provider after capture.
            InvokeButton(f.Window.RoamingStartButton);
            await WaitForLayoutAsync(host, () => f.Library.Snapshot.Source == PlaybackSource.Roaming &&
                f.Library.Snapshot.Playback.Intent == PlaybackIntent.Playing);
            InvokeButton(f.Window.RoamingPauseButton);
            await WaitForLayoutAsync(host, () => f.Library.Snapshot.Playback.Intent == PlaybackIntent.Paused);
            InvokeButton(f.Window.RoamingStopButton);
            await WaitForLayoutAsync(host, () => f.Library.Snapshot.Playback.Intent == PlaybackIntent.Stopped);
            Assert.False((bool)Get(f.Window, "_busy")!);
        }
        finally { host.Close(); }
    });

    /// <summary>把每个画面渲染成 PNG，用于和 .codebuddy/figma/2_2 设计稿逐张比对。
    /// 只在显式运行时执行：dotnet test --filter DesignScreenshots</summary>
    [Fact(Explicit = true)]
    public Task DesignScreenshotsOfEveryPage() => OnSta(async () =>
    {
        using var f = new WindowFixture();
        var directory = Environment.GetEnvironmentVariable("WALLBILI_UI_ARTIFACT_DIR") ??
            Path.Combine(Path.GetTempPath(), "wallbili-design-review");

        // 先捕获「片库还是空的」状态（设计稿画板 06 · 03），再灌入示例内容。
        using (var blank = new WindowFixture())
        {
            await blank.Library.InitializeAsync(Token);
            Set(blank.Window, "_ready", true);
            var blankHost = LoadVisibleFixture(blank.Window);
            try
            {
                Call(blank.Window, "Render");
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blankHost.Loaded += (_, _) => ready.TrySetResult();
                blankHost.Show();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
                var blankTabs = blank.Window.ContentTabs;
                SelectTab((TabItem)blankTabs.Items[1]);
                await WaitForLayoutAsync(blankHost, () => ((TabItem)blankTabs.Items[1]).IsSelected);
                Capture(blankHost, directory, "design-00-empty-library.png");
            }
            finally { blankHost.Close(); }
        }

        await f.Library.InitializeAsync(Token);
        var state = (LibraryState)Get(f.Library, "_state")!;
        // 与设计稿示例一致的内容，方便逐画面比对。
        var seed = new (string Bvid, string Title, string Uploader, int Part, int Minutes)[]
        {
            ("BV1SAMPLE001", "晴天的午后", "房间里的声音", 1, 45),
            ("BV1SAMPLE002", "沿河慢行", "房间里的声音", 2, 32),
            ("BV1SAMPLE003", "雨声和钢琴", "白噪音档案", 3, 28),
            ("BV1SAMPLE004", "凌晨两点", "独立音乐选", 4, 51),
            ("BV1SAMPLE005", "缓慢的星期日", "房间里的声音", 5, 36),
            ("BV1SAMPLE006", "窗外的风", "独立音乐选", 6, 41),
            ("BV1SAMPLE007", "树影晃动", "房间里的声音", 7, 27),
            ("BV1SAMPLE008", "旧唱片的傍晚", "独立音乐选", 8, 35)
        };
        var keys = new List<ContentKey>();
        foreach (var (bvid, title, uploader, part, minutes) in seed)
        {
            var key = new ContentKey(bvid, 100 + part);
            state.Entries.Add(new CatalogEntry(key, title, uploader, part,
                TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(part * 3 + 17),
                part == 8 ? EntryAvailability.Failed : EntryAvailability.Available, part == 8 ? "Inaccessible" : null));
            keys.Add(key);
        }
        state.Sources.Add(new CatalogSource("src-music", "轻音乐日常", null,
            new CollectionSource(900001, 11, CollectionKind.Season), DateTimeOffset.Now,
            [keys[0], keys[1], keys[4], keys[6]], []));
        state.Sources.Add(new CatalogSource("src-focus", "专注时刻", null,
            new CollectionSource(900002, 12, CollectionKind.Series), DateTimeOffset.Now,
            [keys[3], keys[5], keys[7]], []));
        state.Sources.Add(new CatalogSource("src-rain", "雨天白噪音", null,
            new CollectionSource(900003, 13, CollectionKind.Season), DateTimeOffset.Now, [keys[2]], []));
        var creator = new FollowedCreator(900001, "轻音乐日常");
        var now = DateTimeOffset.Now;
        CreatorVideo[] videos = [
            new("BV1SAMPLE101", creator.Uid, creator.Name, "今日新作 · 城市夜景", now.AddHours(-2), TimeSpan.FromMinutes(12)),
            new("BV1SAMPLE102", creator.Uid, creator.Name, "未播旧作 · 雨声与窗景", now.AddDays(-20), TimeSpan.FromMinutes(30))
        ];
        await ((RoamingService)Get(f.Window, "_roaming")!).DisposeAsync();
        var roaming = new RoamingService(f.Library, new VisibleDiscovery(creator, videos));
        Set(f.Window, "_roaming", roaming);
        Set(f.Window, "_roamingOperations", new HashSet<Task>());
        await roaming.SetAccountAsync(creator.Uid, Token);
        await roaming.SaveSubscriptionsAsync([creator], Token);
        await roaming.RefreshAsync(Token);
        await f.Library.EnqueueAsync(keys.Take(6).ToArray(), Token);
        await f.Library.PlayAsync(token: Token);
        Set(f.Window, "_ready", true);

        var host = LoadVisibleFixture(f.Window);
        try
        {
            Call(f.Window, "Render");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Loaded += (_, _) => loaded.TrySetResult();
            host.Show();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var tabs = f.Window.ContentTabs;
            var names = new[] { "01-workbench", "02-library", "03-queue", "04-roaming", "05-desktop" };
            for (var index = 0; index < names.Length; index++)
            {
                SelectTab((TabItem)tabs.Items[index]);
                await WaitForLayoutAsync(host, () => ((TabItem)tabs.Items[index]).IsSelected);
                Capture(host, directory, $"design-{names[index]}.png");
            }

            f.Window.AddSourceOverlay.Visibility = Visibility.Visible;
            await WaitForLayoutAsync(host, () => f.Window.ImportPanel.IsVisible);
            Capture(host, directory, "design-06-import.png");
            TestContext.Current.TestOutputHelper?.WriteLine($"Screenshots={directory}");
        }
        finally { host.Close(); }
    });

    private static void Capture(Window host, string directory, string name)
    {
        var root = (Grid)host.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
        var height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var frame = new DrawingVisual();
        using (var drawing = frame.RenderOpen())
        {
            drawing.DrawRectangle(host.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(root), null,
                new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static Window LoadVisibleFixture(MainWindow target, [CallerFilePath] string source = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
        var document = XDocument.Load(Path.Combine(repo, "src/WallBili.Desktop/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = document.Root!;
        root.Attribute(x + "Class")!.Remove();
        root.Attribute("Loaded")!.Remove(); root.Attribute("Closing")!.Remove();
        var hookups = new List<(string Name, string Event, string Method)>();
        var id = 0;
        foreach (var element in root.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName.Length == 0 &&
                a.Name.LocalName is "Click" or "SelectionChanged" or "MouseDoubleClick").ToArray())
            {
                var name = element.Attribute(x + "Name");
                if (name is null) { name = new XAttribute(x + "Name", "VisibleTestControl" + ++id); element.Add(name); }
                hookups.Add((name.Value, attribute.Name.LocalName, attribute.Value));
                attribute.Remove();
            }
        }
        var host = (Window)XamlReader.Parse(document.ToString());
        host.Title = "WallBili · 订阅漫游验收（隔离测试数据）";
        host.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var app = XDocument.Load(Path.Combine(repo, "src/WallBili.Desktop/App.xaml"));
        var resources = new XElement(root.Name.Namespace + "ResourceDictionary", app.Root!.Elements().First().Elements());
        // 隔离宿主是从文本重新解析这份字典的，clr-namespace 必须带程序集限定名才找得到 DesignFont；
        // App.xaml 自己不能写 assembly=（同一个程序集在标记编译阶段会报 MC3074），所以在此补全。
        host.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString().Replace(
            "clr-namespace:WallBili.Desktop\"", "clr-namespace:WallBili.Desktop;assembly=WallBili.Desktop\""));
        foreach (var element in root.Descendants().Where(e => e.Attribute(x + "Name") is not null))
        {
            var name = element.Attribute(x + "Name")!.Value;
            if (typeof(MainWindow).GetField(name, Fields) is { } field) field.SetValue(target, host.FindName(name));
        }
        foreach (var hookup in hookups)
        {
            // 模板里的行内按钮不在窗口名字作用域内，隔离窗口不接线（它们的行为由真实窗口覆盖）。
            if (host.FindName(hookup.Name) is not { } control) continue;
            var eventInfo = control.GetType().GetEvent(hookup.Event)!;
            var method = typeof(MainWindow).GetMethod(hookup.Method, Fields | BindingFlags.DeclaredOnly)!;
            eventInfo.AddEventHandler(control, method.CreateDelegate(eventInfo.EventHandlerType!, target));
        }
        return host;
    }

    private static async Task WaitForLayoutAsync(Window host, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            if (condition()) return;
        } while (DateTime.UtcNow < deadline);
        Assert.Fail("The isolated WPF window did not reach the expected visible state before the timeout.");
    }

    private static async Task<TabItem> SelectTabContainingAsync(Window host, TabControl tabs, FrameworkElement child)
    {
        foreach (var item in tabs.Items.OfType<TabItem>())
        {
            SelectTab(item);
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (child.IsVisible) return item;
        }
        throw new InvalidOperationException($"No tab hosts {child.Name}.");
    }

    private static void SelectTab(TabItem tab) =>
        ((ISelectionItemProvider)new TabItemAutomationPeer(tab, new TabControlAutomationPeer((TabControl)tab.Parent))
            .GetPattern(PatternInterface.SelectionItem)!).Select();
    private static void InvokeButton(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    private sealed class VisibleDiscovery(FollowedCreator creator, IReadOnlyList<CreatorVideo> videos) : IContentDiscoveryClient
    {
        public Task<FollowingsPage> GetFollowingsAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FollowingsPage(page, pageSize, 1, [creator]));
        public Task<CreatorVideosPage> GetCreatorVideosAsync(long uploaderId, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CreatorVideosPage(uploaderId, page, pageSize, videos.Count, videos));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StoppingOrClosingAnInFlightQrPollIsSafe(bool close) => OnSta(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/","qrcode_key":"challenge-key-0123456789"}}"""));
            entered.TrySetResult(); return release.Task; // Deliberately ignores cancellation until released.
        }));
        var dialog = (LoginDialog)RuntimeHelpers.GetUninitializedObject(typeof(LoginDialog));
        Set(dialog, "_client", new BilibiliClient(http));
        Set(dialog, "_polls", new CancellationTokenSource());
        Set(dialog, "_timer", new DispatcherTimer { Interval = TimeSpan.FromHours(1) });
        Set(dialog, "QrStatus", new TextBlock()); Set(dialog, "QrImage", new Image());
        var pending = (Task)Call(dialog, "RefreshQrAsync")!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (close) Call(dialog, "Window_Closing", null, new System.ComponentModel.CancelEventArgs());
        else Call(dialog, "StopPolling");
        dialog.QrStatus.Text = "Stopped";
        release.SetResult(Json("""{"code":0,"data":{"code":86101}}"""));

        await pending;
        Assert.Equal("Stopped", dialog.QrStatus.Text);
        Assert.Null(dialog.CookieResult);
        if (!close) Call(dialog, "Window_Closing", null, new System.ComponentModel.CancelEventArgs());
    });

    private static async Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
    }

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Fields | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    private static object? Get(object target, string name) => target.GetType().GetField(name, Fields)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
    private static void Select(ComboBox box, string? id)
    {
        var type = typeof(MainWindow).GetNestedType("QualityRow", BindingFlags.NonPublic)!;
        var row = Activator.CreateInstance(type, Fields, null, new object?[] { id, id ?? "Automatic", false }, null)!;
        box.ItemsSource = new[] { row }; box.SelectedItem = row;
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    /// <summary>Exercises the real window methods without the app singleton, visible windows, tray icon,
    /// mpv, or the user's data directory. Only owned hidden output HWNDs are created and disposed.</summary>
    private sealed class WindowFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wallbili-desktop-test-" + Guid.NewGuid().ToString("N"));
        private readonly DesktopVideoHost _host = new();
        private readonly System.Windows.Forms.NotifyIcon _icon = new() { Visible = false };
        private readonly HttpClient _http;
        public MainWindow Window { get; } = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        public LibrarySession Library { get; }
        public Content Content { get; } = new();
        public AuthStore Auth { get; }
        public Func<HttpResponseMessage> AccountResponse { get; set; }
        public string PreferencesPath => Path.Combine(_directory, "preferences.json");

        public WindowFixture(ContentFailureKind failure = ContentFailureKind.Network)
        {
            Library = new(Content, new Store(), new Player());
            AccountResponse = () => failure == ContentFailureKind.Network
                ? throw new HttpRequestException("offline") : Json("""{"code":-412}""");
            _http = new(new AsyncHandler((_, _) => Task.FromResult(AccountResponse())));
            Auth = new(Path.Combine(_directory, "auth.dat"));
            var client = new BilibiliClient(_http);
            Set(Window, "_client", client); Set(Window, "_authStore", Auth);
            Set(Window, "_library", Library); Set(Window, "_host", _host);
            Set(Window, "_roaming", new RoamingService(Library, client));
            Set(Window, "_roamingOperations", new HashSet<Task>());
            Set(Window, "_roamingTick", Task.CompletedTask); Set(Window, "_roamingAccountTask", Task.CompletedTask);
            var tray = RuntimeHelpers.GetUninitializedObject(typeof(TrayIcon));
            Set(tray, "_icon", _icon); Set(Window, "_tray", tray);
            Set(Window, "_preferences", new DesktopPreferences { MpvPath = "mpv", WallpaperEnabled = false });
            Set(Window, "_preferencesPath", PreferencesPath); Set(Window, "_mpvPath", "mpv");
            Set(Window, "_lifetime", new CancellationTokenSource());
            Set(Window, "_mcpLifecycle", new SemaphoreSlim(1, 1));
            Set(Window, "_mcpTokens", new McpTokenStore(Path.Combine(_directory, "mcp-token.dat")));
            Set(Window, "_agentOperations", new HashSet<Task>());
            Set(Window, "_timer", new DispatcherTimer { Interval = TimeSpan.FromHours(1) });
            foreach (var name in new[] { "ImportPanel", "ListsPanel", "PlayerPanel", "DesktopPanel", "SourcesBox", "CatalogGrid",
                "QueueGrid", "QueueTitle", "CurrentText", "PositionText", "Progress", "ModeBox", "StatusText", "DesktopStatus",
                "VideoQualityBox", "AudioQualityBox", "QualityStatusText", "AccountText", "LoginButton", "VolumeSlider",
                "RoamingPanel", "RoamingManageButton", "RoamingRefreshButton", "RoamingStartButton", "RoamingPauseButton",
                "RoamingStopButton", "RoamingNextButton", "RoamingScheduleSaveButton", "RoamingScheduleBox", "RoamingScheduleInput",
                "RoamingNextScheduleText", "RoamingPendingPanel", "RoamingSummaryText", "RoamingGrid", "RoamingStatusText",
                "PlaybackSourceText", "QueueUpButton", "QueueDownButton", "RemoveQueueButton" })
                Set(Window, name, Activator.CreateInstance(typeof(MainWindow).GetField(name, Fields)!.FieldType));
            Set(Window, "MpvPathInput", new TextBox { Text = "mpv" });
            Window.ModeBox.Items.Add("Sequential"); Window.ModeBox.Items.Add("Repeat");
        }

        public void Dispose()
        {
            ((DispatcherTimer)Get(Window, "_timer")!).Stop();
            var lifetime = (CancellationTokenSource)Get(Window, "_lifetime")!;
            lifetime.Cancel();
            ((Task)Call(Window, "StopAgentAsync")!).GetAwaiter().GetResult();
            lifetime.Dispose();
            ((RoamingService)Get(Window, "_roaming")!).DisposeAsync().AsTask().GetAwaiter().GetResult();
            Library.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _host.Dispose(); _icon.Dispose(); _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class Content : IContentClient
    {
        public MediaSelectionPreferences? LastSelection { get; private set; }
        public Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default) => Task.FromResult(
            new VideoInfo(input, "Test", "Uploader", 1, TimeSpan.FromSeconds(60), [new(1, new(input, 101), "Test", TimeSpan.FromSeconds(60))]));
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default) =>
            ResolveMediaAsync(key, MediaSelectionPreferences.Automatic, cancellationToken);
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, MediaSelectionPreferences preferences, CancellationToken cancellationToken = default)
        {
            LastSelection = preferences;
            return Task.FromResult(new MediaSession(key, new(new Uri("https://media.example/video"), "avc", 80), null,
                new Dictionary<string, string>(), DateTimeOffset.UtcNow, null));
        }
        public Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed class Store : ILibraryStore
    {
        public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LibraryState());
        public Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Player : IQueuePlayer
    {
        public event EventHandler<QueuePlaybackEnded>? Ended { add { } remove { } }
        public Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult<TimeSpan?>(TimeSpan.Zero);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
