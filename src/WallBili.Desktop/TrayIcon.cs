using System.Drawing;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WallBili.Desktop.Presentation;
using Forms = System.Windows.Forms;

namespace WallBili.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly Forms.ContextMenuStrip _nativeMenuBridge;
    public TrayIcon(Dispatcher dispatcher, Action show, Action playPause, Action next, Action toggleWallpaper, Action exit)
    {
        void Dispatch(Action action) => dispatcher.BeginInvoke(action);
        _menu = new() { FontFamily = UiTypography.Family, FontSize = 13, Placement = PlacementMode.MousePoint };
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { _menu.IsOpen = false; Dispatch(action); };
            _menu.Items.Add(item);
        }
        Add("打开 WallBili", show);
        Add("播放 / 暂停", playPause);
        Add("下一项", next);
        Add("显示 / 隐藏桌面画面", toggleWallpaper);
        _menu.Items.Add(new Separator());
        Add("退出 WallBili", exit);
        // Preserve NotifyIcon's mouse/keyboard menu entry points, but never draw its GDI menu.
        _nativeMenuBridge = new();
        _nativeMenuBridge.Opening += (_, args) => { args.Cancel = true; dispatcher.BeginInvoke(() => _menu.IsOpen = true); };
        _icon = new() { Text = "WallBili", Icon = SystemIcons.Application, ContextMenuStrip = _nativeMenuBridge, Visible = true };
        _icon.DoubleClick += (_, _) => Dispatch(show);
    }
    public void SetStatus(string title)
    {
        // NotifyIcon's text limit is 63 UTF-16 characters; avoid cutting a surrogate pair.
        var value = "WallBili · " + title;
        if (value.Length > 63) value = value[..(char.IsHighSurrogate(value[62]) ? 62 : 63)];
        _icon.Text = value;
    }
    public void Dispose() { _menu.IsOpen = false; _icon.Visible = false; _icon.Dispose(); _nativeMenuBridge.Dispose(); }
}
