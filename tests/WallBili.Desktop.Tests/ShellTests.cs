using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallBili.Core;
using WallBili.Desktop.Presentation;
using WallBili.Desktop.Views;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class ShellTests
{
    [Fact]
    public Task SelectionSurvivesCheckpointsAndSearchFiltersBothLists() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.CatalogRows[0].IsSelected = true; vm.CatalogRows[1].IsSelected = true;
        var first = vm.CatalogRows[0];
        await library.CheckpointAsync();
        Assert.Same(first, vm.CatalogRows[0]);
        Assert.Equal(2, vm.SelectedCount);
        vm.SearchText = "雨声";
        Assert.Single(vm.CatalogRows); Assert.Single(vm.QueueRows);
        Assert.Equal("雨声和钢琴", vm.CatalogRows[0].Title);
        vm.SearchText = "";
        Assert.Equal(2, vm.SelectedCount);
        vm.StatusFilter = "failed";
        Assert.Single(vm.CatalogRows); Assert.Equal("不可访问", vm.CatalogRows[0].Status);
    });

    [Fact]
    public Task BatchEnqueueUsesOnlyTheSelectedVisibleEntriesInOrder() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.CatalogRows[1].IsSelected = true; vm.CatalogRows[3].IsSelected = true;
        var expected = new[] { vm.CatalogRows[1].Key, vm.CatalogRows[3].Key };
        var before = library.Snapshot.Queue.Count;
        await Execute(vm.AddSelectedCommand);
        Assert.Equal(before + 2, library.Snapshot.Queue.Count);
        Assert.Equal(expected, library.Snapshot.Queue.TakeLast(2).Select(q => q.Key));
        vm.SourceFilter = "Season:3:30";
        Assert.Single(vm.CatalogRows);
        Assert.False(vm.HasSelection);
        Assert.False(vm.AddSelectedCommand.CanExecute(null));
    });

    [Fact]
    public Task QueueActionsUseStableIdsAndRealSessionPlaybackState() => UiThread.Run(async () =>
    {
        var player = new PreviewSession.Player();
        await using var library = NewLibrary(player);
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        await Execute(vm.PlayPauseCommand);
        Assert.False(vm.IsPlaying); Assert.True(player.Paused);
        vm.SelectedQueue = vm.QueueRows[2];
        var id = vm.SelectedQueue.Id;
        await Execute(vm.MoveQueueUpCommand);
        Assert.Equal(id, library.Snapshot.Queue[1].Id);
        await Execute(vm.PlayQueueCommand);
        Assert.Equal(id, library.Snapshot.Playback.CurrentId);
        Assert.True(vm.IsPlaying);
        vm.IsSeeking = true; vm.SeekPosition = 123;
        await Execute(vm.SeekCommand);
        Assert.Equal(TimeSpan.FromSeconds(123), player.Position);
        await Execute(vm.RemoveQueueCommand);
        Assert.DoesNotContain(library.Snapshot.Queue, q => q.Id == id);
        Assert.Equal(8, library.Snapshot.Entries.Count);
    });

    [Fact]
    public Task InvalidImportKeepsTheDialogAndSuccessfulImportOpensTheLibrary() => UiThread.Run(async () =>
    {
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(new()), new PreviewSession.Player());
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.OpenImportCommand.Execute(null); vm.ImportInput = "not-a-link";
        await Execute(vm.ImportCommand);
        Assert.True(vm.IsImportOpen); Assert.NotEmpty(vm.ImportError); Assert.Empty(library.Snapshot.Sources);
        vm.ImportInput = "BV1xx411c7mD";
        await Execute(vm.ImportCommand);
        Assert.False(vm.IsImportOpen); Assert.True(vm.IsLibrary);
        Assert.Single(library.Snapshot.Sources); Assert.Equal(3, vm.CatalogRows.Count);
        Assert.Empty(library.Snapshot.Queue);
    });

    [Fact]
    public Task ImportByIdRejectsBadIdsAndSourceRemovalRetainsCatalog() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.OpenImportCommand.Execute(null); vm.SetImportModeCommand.Execute("ids");
        vm.UploaderId = "-1"; vm.CollectionId = "10";
        await Execute(vm.ImportCommand);
        Assert.Contains("正整数", vm.ImportError);
        vm.UploaderId = "1";
        await Execute(vm.ImportCommand);
        Assert.False(vm.IsImportOpen);
        var entries = library.Snapshot.Entries.Count; var queue = library.Snapshot.Queue.Count;
        await Execute(vm.RemoveSourceCommand, vm.SourceRows[0]);
        Assert.Equal(entries, library.Snapshot.Entries.Count); Assert.Equal(queue, library.Snapshot.Queue.Count);
    });

    [Fact]
    public Task SettingsAndQualityAreAppliedThroughTheirServiceBoundaries() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        DesktopPreferences? saved = null; bool startup = false;
        using var vm = new ShellViewModel(library, new(), (p, s) => { saved = p; startup = s; return Task.CompletedTask; }, preview: true);
        await library.InitializeAsync();
        vm.IsWallpaperEnabled = false; vm.ContinueAudio = true; vm.AutoStart = true; vm.MpvPath = "custom-mpv.exe";
        await Execute(vm.ApplySettingsCommand);
        Assert.NotNull(saved); Assert.False(saved.WallpaperEnabled); Assert.True(saved.ContinueAudioWhenHidden);
        Assert.True(startup); Assert.Equal("custom-mpv.exe", saved.MpvPath);
        vm.VideoQualityId = "64";
        var id = library.Snapshot.Playback.CurrentId;
        await Execute(vm.ApplyQualityCommand);
        Assert.Equal("64", saved.PreferredVideoQualityId);
        Assert.Equal(id, library.Snapshot.Playback.CurrentId);
        Assert.Equal("64", library.Snapshot.Media?.SelectedVideo);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreviewBootNeverCreatesDataFilesOrConnectsToTheRealClient(bool empty) => UiThread.Run(async () =>
    {
        var options = StartupOptions.Parse(empty ? ["--empty"] : ["--preview"]);
        Assert.False(Directory.Exists(options.DataDirectory));
        await using var runtime = new DesktopSession(options);
        await runtime.InitializeAsync();
        Assert.Null(runtime.Client);
        Assert.True(runtime.Library.IsInitialized);
        Assert.Equal(empty, runtime.ViewModel.IsEmpty);
        await Execute(runtime.ViewModel.ApplySettingsCommand);
        Assert.False(Directory.Exists(options.DataDirectory));
    });

    [Fact]
    public Task CheckpointDoesNotOverwriteTheVolumeWhileTheUserIsDragging() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.IsAdjustingVolume = true; vm.Volume = 42;
        await library.CheckpointAsync();
        Assert.Equal(42, vm.Volume);
        vm.IsAdjustingVolume = false;
        await Execute(vm.VolumeCommand);
        Assert.Equal(42, library.Snapshot.Playback.Volume);
    });

    [Fact]
    public void ProfilesHaveSeparateStorageAndInstanceSignals()
    {
        var normal = StartupOptions.Parse([]);
        Assert.True(normal.IsIsolated);
        Assert.EndsWith(Path.Combine("profiles", "design-rebuild"), normal.DataDirectory);
        var other = StartupOptions.Parse(["--data-dir", Path.Combine(Path.GetTempPath(), "wallbili other profile")]);
        Assert.NotEqual(normal.InstanceId, other.InstanceId);
        var same = StartupOptions.Parse(["--data-dir", normal.DataDirectory.ToUpperInvariant()]);
        Assert.Equal(normal.InstanceId, same.InstanceId);
        Assert.NotEqual(StartupOptions.Parse(["--preview"]).InstanceId, StartupOptions.Parse(["--preview"]).InstanceId);
        Assert.Contains("--data-dir \"" + other.DataDirectory + "\"", new StartupRegistration(other).Command());
    }

    [Fact]
    public Task TypographyRoutesChineseSeparatelyFromLatinAndNumbers() => UiThread.Run(() =>
    {
        var maps = UiTypography.Family.FamilyMaps;
        Assert.Equal(UiTypography.HanFamily, maps[0].Target);
        Assert.Equal(UiTypography.LatinFamily, maps[1].Target);
        Assert.NotEqual(UiTypography.HanFamily, UiTypography.LatinFamily);
        Assert.True(new Typeface(new FontFamily(UiTypography.HanFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var han));
        Assert.True(han.CharacterToGlyphMap.ContainsKey('播'));
        Assert.True(new Typeface(new FontFamily(UiTypography.LatinFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var latin));
        Assert.True(latin.CharacterToGlyphMap.ContainsKey('W'));
        var directory = Environment.GetEnvironmentVariable("WALLBILI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "font-map.json"), System.Text.Json.JsonSerializer.Serialize(new { Han = UiTypography.HanFamily, Latin = UiTypography.LatinFamily, HanGlyphFamily = han.FamilyNames.Values, LatinGlyphFamily = latin.FamilyNames.Values }));
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task RealWpfPagesRenderAtThreeSizesAndBoundNavigationWorks() => UiThread.Run(async () =>
    {
        await using var library = NewLibrary();
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        vm.SetDisplays([new("preview-display", 0, 0, 2560, 1440, true)], false);
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = -18000 };
        window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(UiTypography.Family, window.FontFamily);
            var workspace = Descendants<WorkspaceView>(window.ShellRoot).Single();
            workspace.WorkspaceMode.SelectedValue = "sequential";
            await Arrange(window);
            Assert.Equal(QueueMode.Sequential, library.Snapshot.Playback.Mode);
            workspace.WorkspaceMode.SelectedValue = "repeat";
            await Arrange(window);
            foreach (var (width, height) in new[] { (1440, 960), (1180, 820), (1024, 720) })
            {
                window.Width = width; window.Height = height;
                foreach (var page in new[] { "Workspace", "Library", "Queue", "Settings" })
                {
                    vm.Navigate(page);
                    await Arrange(window);
                    Assert.True(window.ShellRoot.ActualWidth > 0);
                    AssertBounds(window, window.AddSourceButton, width, height);
                    AssertBounds(window, window.PlayPauseButton, width, height);
                    Assert.DoesNotContain(Descendants<TextBlock>(window.ShellRoot).Where(t => t.IsVisible), t => t.Text.Contains("SelectOption {"));
                    Capture(window.ShellRoot, $"{page}-{width}x{height}.png");
                }
            }
            window.Width = 1440; window.Height = 960;
            var nav = window.LibraryNav;
            nav.Command.Execute(nav.CommandParameter);
            Assert.True(vm.IsLibrary);
            await Arrange(window);
            var catalog = Descendants<LibraryView>(window.ShellRoot).Single().CatalogGrid;
            catalog.SelectedItems.Add(vm.CatalogRows[0]); catalog.SelectedItems.Add(vm.CatalogRows[1]);
            Assert.Equal(2, vm.SelectedCount);
            await Arrange(window); Capture(window.ShellRoot, "Library-selected.png");
            window.AddSourceButton.Command.Execute(null);
            await Arrange(window);
            Assert.True(window.ImportOverlay.IsVisible);
            Assert.False(window.LibraryNav.IsEnabled);
            Capture(window.ShellRoot, "Import-video.png");
            vm.SetImportModeCommand.Execute("collection");
            await Arrange(window); Capture(window.ShellRoot, "Import-collection.png");
            vm.SetImportModeCommand.Execute("ids");
            await Arrange(window); Capture(window.ShellRoot, "Import-ids.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EmptyLibraryRendersRealEmptyStatesWithoutSampleData() => UiThread.Run(async () =>
    {
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(new()), new PreviewSession.Player());
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = -18000 };
        window.Show();
        try
        {
            foreach (var page in new[] { "Workspace", "Library", "Queue" })
            {
                vm.Navigate(page); await Arrange(window); Capture(window.ShellRoot, "Empty-" + page + ".png");
                Assert.False(vm.PlayPauseCommand.CanExecute(null));
                Assert.Empty(vm.CatalogRows); Assert.Empty(vm.QueueRows);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LoadingQualityOptionsAfterTheWindowOpensKeepsTheStoredPreference() => UiThread.Run(async () =>
    {
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(PreviewSession.Seed()),
            new PreviewSession.Player(), selection: new("64", "30280"));
        using var vm = new ShellViewModel(library, new() { PreferredVideoQualityId = "64", PreferredAudioQualityId = "30280" }, preview: true);
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = -18000 };
        window.Show();
        try
        {
            vm.Navigate("Settings"); await Arrange(window);
            await library.InitializeAsync(); await Arrange(window);
            Assert.Equal("64", vm.VideoQualityId); Assert.Equal("30280", vm.AudioQualityId);
            Assert.Equal("64", library.Snapshot.Media?.SelectedVideo);
        }
        finally { window.Close(); }
    });

    private static LibrarySession NewLibrary(PreviewSession.Player? player = null) => new(new PreviewSession.Content(), new PreviewSession.Store(PreviewSession.Seed()), player ?? new PreviewSession.Player());
    private static Task Execute(ICommand command, object? parameter = null) => ((AsyncUiCommand)command).ExecuteAsync(parameter);
    private static async Task Arrange(MainWindow window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void AssertBounds(MainWindow window, FrameworkElement element, double width, double height)
    {
        if (!element.IsVisible) return;
        var bounds = element.TransformToAncestor(window.ShellRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1, $"{element.Name} lies outside {width}x{height}: {bounds}");
    }
    private static void Capture(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WALLBILI_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth), (int)Math.Round(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}

internal static class UiThread
{
    private static readonly Lazy<Task<Dispatcher>> Ready = new(() =>
    {
        var completion = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WallBili.Desktop;component/Presentation/Theme.xaml") });
                completion.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "WallBili isolated WPF tests" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    });
    internal static async Task Run(Func<Task> action)
    {
        var dispatcher = await Ready.Value.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(60));
    }
}
