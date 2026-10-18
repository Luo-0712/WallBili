using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WallBili.Desktop.Presentation;
using Xunit;
using Forms = System.Windows.Forms;

namespace WallBili.Desktop.Tests;

public sealed class StrokeRenderingTests
{
    // RenderTargetBitmap overrides the text filtering path. This opt-in regression captures
    // real HWND pixels on every attached monitor, without activating or moving the user's app.
    [Fact]
    public Task ApplicationTextKeepsSymmetricStrokesAcrossMonitors() => UiThread.Run(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("WALLBILI_FONT_NATIVE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var grid = new Grid { Background = Brushes.White, Margin = new Thickness(24) };
            for (var c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            var column = 0;
            foreach (var mode in new[] { "Fixed (before)", "Application", "Symmetric reference", "Outline reference" })
            {
                var panel = new StackPanel(); Grid.SetColumn(panel, column++); grid.Children.Add(panel);
                panel.Children.Add(new TextBlock { Text = mode, FontSize = 16, Margin = new Thickness(0, 0, 0, 20) });
                if (mode != "Application")
                {
                    TextOptions.SetTextRenderingMode(panel, TextRenderingMode.Grayscale);
                    TextOptions.SetTextHintingMode(panel, mode == "Symmetric reference" ? TextHintingMode.Animated : TextHintingMode.Fixed);
                }
                foreach (var size in new[] { 11.0, 12, 13, 14, 16, 24 })
                {
                    var content = size < 16 ? "三工田 我的片库 音乐" : "WallBili 01248";
                    if (mode == "Outline reference")
                    {
                        var text = new FormattedText(content, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface(UiTypography.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1);
                        panel.Children.Add(new System.Windows.Shapes.Path { Fill = Brushes.Black, Data = text.BuildGeometry(new Point(0, 0)),
                            Height = 46, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
                    }
                    else panel.Children.Add(new TextBlock { Text = content, FontFamily = UiTypography.Family, FontSize = size,
                        Height = 46, VerticalAlignment = VerticalAlignment.Top });
                }
            }
            var window = new Window { Content = grid, Width = 1248, Height = 400, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                // HWND_BOTTOM + NOACTIVATE: keep the probe behind the user's windows.
                Assert.True(SetWindowPos(hwnd, new IntPtr(1), screen.Bounds.Left + 24, screen.Bounds.Top + 24, 0, 0, 0x0011));
                window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
                GetWindowRect(hwnd, out var rect);
                using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try { Assert.True(PrintWindow(hwnd, hdc, 2)); }
                    finally { graphics.ReleaseHdc(hdc); }
                }
                var dpi = GetDpiForWindow(hwnd); var scale = dpi / 96.0; var mismatch = 0; var changed = 0;
                // Column offsets are integral pixels at the tested 125% and 150% scales.
                // Allow two 8-bit rounding steps in native blending, but no stroke changes.
                for (var y = (int)(68 * scale); y < (int)(330 * scale); y++)
                for (var x = (int)(24 * scale); x < (int)(280 * scale); x++)
                {
                    var before = bitmap.GetPixel(x, y);
                    var actual = bitmap.GetPixel(x + (int)(300 * scale), y);
                    var expected = bitmap.GetPixel(x + (int)(600 * scale), y);
                    if (Math.Abs(actual.R - expected.R) > 2 || Math.Abs(actual.G - expected.G) > 2 || Math.Abs(actual.B - expected.B) > 2) mismatch++;
                    if (Math.Abs(actual.R - before.R) > 8) changed++;
                }
                var name = screen.DeviceName.Replace('\\', '_').Replace('.', '_');
                bitmap.Save(Path.Combine(directory, $"native-strokes-{dpi}-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
                results.Add(new { Screen = screen.DeviceName, Dpi = dpi, MismatchedPixels = mismatch, ChangedPixels = changed });
                Assert.True(changed > 100, $"At {dpi} DPI the probe did not expose the original stroke-snapping symptom.");
                Assert.True(mismatch == 0, $"At {dpi} DPI application strokes differ from symmetric rasterization in {mismatch} pixels.");
            }
            finally { window.Close(); }
        }
        File.WriteAllText(Path.Combine(directory, "native-strokes.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    });

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
