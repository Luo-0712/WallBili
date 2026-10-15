using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WallBili.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    public TrayIcon(Dispatcher dispatcher, Action show, Action playPause, Action next, Action toggleWallpaper, Action exit)
    {
        void Dispatch(Action action) => dispatcher.BeginInvoke(action);
        _menu = new();
        _menu.Items.Add("打开 WallBili", null, (_, _) => Dispatch(show));
        _menu.Items.Add("播放 / 暂停", null, (_, _) => Dispatch(playPause));
        _menu.Items.Add("下一项", null, (_, _) => Dispatch(next));
        _menu.Items.Add("显示 / 隐藏桌面画面", null, (_, _) => Dispatch(toggleWallpaper));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出 WallBili", null, (_, _) => Dispatch(exit));
        _icon = new() { Text = "WallBili", Icon = SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => Dispatch(show);
    }
    public void SetStatus(string title)
    {
        // NotifyIcon's text limit is 63 UTF-16 characters; avoid cutting a surrogate pair.
        var value = "WallBili · " + title;
        if (value.Length > 63) value = value[..(char.IsHighSurrogate(value[62]) ? 62 : 63)];
        _icon.Text = value;
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
