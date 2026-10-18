using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallBili.Core;
using WallBili.Desktop.Presentation;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class TypographyTests
{
    [Fact]
    public Task TooltipsUseTheApplicationFontInsteadOfTheSystemMessageFont() => UiThread.Run(() =>
    {
        var tooltip = new ToolTip { Content = "播放下一项 Next 12:48" };
        tooltip.ApplyTemplate();
        Assert.Same(UiTypography.Family, tooltip.FontFamily);
        return Task.CompletedTask;
    });

    [Fact]
    public Task LatinBodyAndHeadingUseTheDesignersRegularAndSemiboldFaces() => UiThread.Run(async () =>
    {
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(PreviewSession.Seed()), new PreviewSession.Player());
        using var vm = new ShellViewModel(library, new(), preview: true); await library.InitializeAsync();
        var window = new MainWindow(vm) { Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var glyphs = Inspect(window.ShellRoot, "weight-regression").Where(g => g.Text.Contains("WallBili")).ToArray();
            Assert.Equal("Regular", glyphs.Single(g => g.Text == "WallBili for Windows").Face);
            Assert.Equal("Semibold", glyphs.Single(g => g.Text == "WallBili").Face);
            Assert.All(glyphs, glyph => Assert.Equal("None", glyph.Simulations));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TextUsesNaturalMetricsAndGrayscaleAntialiasing() => UiThread.Run(() =>
    {
        var window = SampleWindow(new TextBlock { Text = "字形与间距 WallBili 12:48" });
        window.ApplyTemplate();
        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(window));
        Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(window));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ChineseHeadingsUseTheDistributedMediumFace() => UiThread.Run(async () =>
    {
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(PreviewSession.Seed()), new PreviewSession.Player());
        using var vm = new ShellViewModel(library, new(), preview: true); await library.InitializeAsync();
        var window = new MainWindow(vm) { Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var headings = Inspect(window.ShellRoot, "headings").Where(g => g.Family.Contains("Noto Sans SC") && g.Size >= 16).ToArray();
            Assert.NotEmpty(headings);
            Assert.All(headings, glyph => Assert.Equal(500, glyph.Weight));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InterfaceGlyphsComeFromTheDistributedFontDirectory() => UiThread.Run(async () =>
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "播放工作台 WallBili 12:48", FontSize = 24 });
        panel.Children.Add(new TextBox { Name = "AuditInput", Text = "输入内容 Input 123", Width = 500 });
        panel.Children.Add(new Button { Content = "添加来源 Add source" });
        panel.Children.Add(new ComboBox { ItemsSource = new[] { "全部来源 All sources" }, SelectedIndex = 0 });
        var window = SampleWindow(panel); window.Show();
        try
        {
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var glyphs = Inspect(panel, "distribution").Where(g => g.Text.Any(char.IsLetterOrDigit)).ToArray();
            Assert.NotEmpty(glyphs);
            Assert.Contains(glyphs, glyph => glyph.Name == "AuditInput");
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fonts"));
            Assert.All(glyphs, glyph => Assert.StartsWith(expected, new Uri(glyph.FontUri).LocalPath, StringComparison.OrdinalIgnoreCase));
        }
        finally { window.Close(); }
    });

    private static Window SampleWindow(UIElement content) => new() { Content = content, Width = 740, Height = 400,
        FontFamily = UiTypography.Family, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual,
        ShowActivated = false, ShowInTaskbar = false };

    [Fact]
    public Task TrayMenuUsesMixedScriptFontsAndKeepsItsActions() => UiThread.Run(async () =>
    {
        var opened = 0;
        using var tray = new TrayIcon(Dispatcher.CurrentDispatcher, () => opened++, () => { }, () => { }, () => { }, () => { });
        var field = typeof(TrayIcon).GetField("_menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var menu = Assert.IsType<ContextMenu>(field.GetValue(tray));
        menu.ApplyTemplate();
        Assert.Same(UiTypography.Family, menu.FontFamily);
        var item = Assert.IsType<MenuItem>(menu.Items[0]);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, opened);
    });

    [Fact]
    public Task LoginCaptionIsRenderedByTheApplicationFontPipeline() => UiThread.Run(() =>
    {
        using var http = new HttpClient();
        var login = new LoginDialog(new BilibiliClient(http));
        Assert.Equal(WindowStyle.None, login.WindowStyle);
        Assert.Same(UiTypography.Family, login.FontFamily);
        login.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task InspectActualGlyphRunsAndRenderingCandidates() => UiThread.Run(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("WALLBILI_FONT_AUDIT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var records = new List<GlyphRecord>();
        await using var library = new LibrarySession(new PreviewSession.Content(), new PreviewSession.Store(PreviewSession.Seed()), new PreviewSession.Player());
        using var vm = new ShellViewModel(library, new(), preview: true);
        await library.InitializeAsync();
        var window = new MainWindow(vm) { Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            foreach (var page in new[] { "Workspace", "Library", "Queue", "Settings" })
            {
                vm.Navigate(page); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                records.AddRange(Inspect(window.ShellRoot, page));
            }
            vm.Navigate("Library"); vm.OpenImportCommand.Execute(null);
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            records.AddRange(Inspect(window.ShellRoot, "Import"));
            vm.CloseImportCommand.Execute(null); vm.Navigate("Workspace");
            foreach (var (format, render) in new[] { (TextFormattingMode.Display, TextRenderingMode.ClearType), (TextFormattingMode.Ideal, TextRenderingMode.ClearType), (TextFormattingMode.Ideal, TextRenderingMode.Grayscale) })
            {
                TextOptions.SetTextFormattingMode(window, format); TextOptions.SetTextRenderingMode(window, render);
                window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Save(window.ShellRoot, Path.Combine(directory, $"Workspace-{format}-{render}.png"));
            }
        }
        finally { window.Close(); }
        var sample = new StackPanel();
        foreach (var (role, family, weight) in new[] { ("正文", UiTypography.Family, FontWeights.Normal), ("强调", UiTypography.EmphasisFamily, FontWeights.Medium), ("标题", UiTypography.StrongFamily, FontWeights.Medium) })
            sample.Children.Add(new TextBlock { Text = "播放工作台 WallBili 12:48 · " + role, FontSize = 24, FontFamily = family, FontWeight = weight, Margin = new Thickness(0, 8, 0, 8) });
        sample.Children.Add(new Button { Content = "添加来源 Add source", HorizontalAlignment = HorizontalAlignment.Left });
        sample.Children.Add(new TextBox { Text = "输入内容 TextBox 123", Margin = new Thickness(0, 10, 0, 0) });
        var sampleSurface = new Border { Background = Brushes.White, Padding = new Thickness(24), Child = sample };
        var sampleWindow = SampleWindow(sampleSurface);
        sampleWindow.Show();
        try { sampleWindow.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); records.AddRange(Inspect(sample, "Samples")); Save(sampleSurface, Path.Combine(directory, "Samples.png")); }
        finally { sampleWindow.Close(); }
        var faces = new[] { UiTypography.HanFamily, UiTypography.LatinFamily }.Select(name => new
        {
            Family = name, Faces = new FontFamily(name).GetTypefaces().Select(face => new { Weight = face.Weight.ToOpenTypeWeight(), Names = face.FaceNames.Values,
                face.IsBoldSimulated, File = face.TryGetGlyphTypeface(out var glyph) ? glyph.FontUri.ToString() : "none" }).ToArray()
        });
        var tooltip = new ToolTip { Content = "提示文字 Tooltip 123" }; tooltip.ApplyTemplate();
        var menu = new ContextMenu(); menu.ApplyTemplate();
        File.WriteAllText(Path.Combine(directory, "glyph-audit.json"), JsonSerializer.Serialize(new { Fonts = faces, Glyphs = records,
            PopupFonts = new { Tooltip = tooltip.FontFamily.FamilyMaps.Select(m => m.Target), TrayMenu = menu.FontFamily.FamilyMaps.Select(m => m.Target) } }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotEmpty(records);
        var bundledDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fonts"));
        Assert.All(records, glyph => Assert.StartsWith(bundledDirectory, new Uri(glyph.FontUri).LocalPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(records, glyph => Assert.Equal("None", glyph.Simulations));
    });

    internal static IEnumerable<GlyphRecord> Inspect(DependencyObject root, string page)
    {
        if (root is UIElement { IsVisible: false }) yield break;
        if (root is Visual visual)
        {
            var name = root is FrameworkElement element ? element.Name : "";
            for (DependencyObject? parent = root; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is TextBox input) { name = input.Name; break; }
            foreach (var run in Runs(VisualTreeHelper.GetDrawing(visual)))
            {
                var face = run.GlyphTypeface;
                yield return new(page, root.GetType().Name, name, run.Characters is null ? "" : new string(run.Characters.ToArray()),
                    string.Join(" / ", face.FamilyNames.Values.Distinct()), string.Join(" / ", face.FaceNames.Values.Distinct()),
                    face.Weight.ToOpenTypeWeight(), face.StyleSimulations.ToString(), face.FontUri.ToString(), run.FontRenderingEmSize);
            }
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var record in Inspect(VisualTreeHelper.GetChild(root, i), page)) yield return record;
    }
    private static IEnumerable<GlyphRun> Runs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing { GlyphRun: { } run }) yield return run;
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var nested in Runs(child)) yield return nested;
    }
    internal static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    internal sealed record GlyphRecord(string Page, string Element, string Name, string Text, string Family, string Face, int Weight, string Simulations, string FontUri, double Size);
}
