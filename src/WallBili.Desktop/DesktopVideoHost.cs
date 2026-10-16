using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WallBili.Desktop;

public sealed record DisplayTarget(string Id, int Left, int Top, int Width, int Height, bool Primary)
{
    public string Label => Id.Length == 0 ? "自动选择主屏" : $"{Id} · {Width}×{Height}{(Primary ? " · 主屏" : "")}";
}

/// <summary>
/// All calls run on the WPF dispatcher. Only the owned child is reparented or moved.
/// Existing Shell/Wallpaper Engine windows are never created, restacked or modified.
/// </summary>
internal sealed class DesktopVideoHost : IDisposable
{
    private readonly HwndSource _parking;
    private HwndSource? _output;
    private nint _parent;
    private uint _parentPid;
    public bool Attached { get; private set; }
    public string Status { get; private set; } = "正在连接桌面";
    public nint Handle => _output?.Handle ?? 0;

    public DesktopVideoHost()
    {
        _parking = new(new HwndSourceParameters("WallBili hidden video parking")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            Width = 16, Height = 16
        });
        CreateOutput();
    }

    private void CreateOutput()
    {
        _output?.Dispose();
        _output = new(new HwndSourceParameters("WallBili owned desktop video")
        {
            ParentWindow = _parking.Handle, WindowStyle = 0x40000000,
            ExtendedWindowStyle = 0x08000020, Width = 16, Height = 16
        });
        _output.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0084) { handled = true; return -1; } // HTTRANSPARENT, icons receive clicks.
            if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
            return 0;
        });
        _parent = _parking.Handle; _parentPid = 0;
    }

    /// <returns>True only when the video HWND changed and the player must recreate its output.</returns>
    public bool Refresh(bool enabled, string? displayId, bool hasVideo)
    {
        var recreated = _output is null || _output.IsDisposed || !Native.IsWindow(Handle);
        if (recreated) CreateOutput();
        Attached = false;
        var displays = GetDisplays();
        var target = displays.FirstOrDefault(d => d.Id == displayId) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
        var parent = enabled ? FindExistingWallpaperHost() : 0;
        if (parent == 0 || target is null)
        {
            Native.ShowWindow(Handle, 0);
            if (Native.GetParent(Handle) != _parking.Handle) Native.SetParent(Handle, _parking.Handle);
            _parent = _parking.Handle; _parentPid = 0;
            Status = enabled ? "等待可用的 Explorer 壁纸层，自动重试" : "桌面画面已关闭";
            return recreated;
        }
        Native.GetWindowThreadProcessId(parent, out var pid);
        if (Native.GetParent(Handle) != parent || _parent != parent || _parentPid != pid)
        {
            Native.ShowWindow(Handle, 0);
            Native.SetParent(Handle, parent);
            if (Native.GetParent(Handle) != parent)
            {
                Status = "桌面绑定未完成，等待重试";
                return recreated;
            }
            _parent = parent; _parentPid = pid;
        }
        var origin = new Native.Point { X = target.Left, Y = target.Top };
        Native.MapWindowPoints(0, parent, ref origin, 1);
        // SWP_NOZORDER | SWP_NOACTIVATE: do not reorder any sibling or activate the host.
        if (!Native.SetWindowPos(Handle, 0, origin.X, origin.Y, target.Width, target.Height, 0x0010 | 0x0004))
        {
            Native.ShowWindow(Handle, 0);
            Status = "显示器布局正在变化，等待重试";
            return recreated;
        }
        Native.ShowWindow(Handle, hasVideo ? 4 : 0); // SW_SHOWNOACTIVATE; never cover the desktop with an idle black host.
        Attached = true;
        Status = $"{(hasVideo ? "桌面画面" : "桌面已就绪")} · {target.Label}";
        if (displayId is { Length: > 0 } && target.Id != displayId) Status += "（所选屏幕离线，暂用主屏）";
        return recreated;
    }

    private static nint FindExistingWallpaperHost()
    {
        nint found = 0;
        Native.EnumWindows((window, _) =>
        {
            var defView = Native.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (defView == 0 || !IsExplorer(window)) return true;
            // Classic layout: the host is a top-level WorkerW after the window owning the icons.
            found = NextWorkerHost(0, window);
            // Windows 11 also nests it: a WorkerW sibling that follows SHELLDLL_DefView in the same parent.
            if (found == 0) found = NextWorkerHost(window, defView);
            return found == 0;
        }, 0);
        return found;
    }

    private static nint NextWorkerHost(nint parent, nint after)
    {
        // Keep looking past a WorkerW that only hosts the icons instead of giving up on it.
        for (var candidate = Native.FindWindowEx(parent, after, "WorkerW", null); candidate != 0;
            candidate = Native.FindWindowEx(parent, candidate, "WorkerW", null))
            if (Native.FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) == 0 && IsExplorer(candidate))
                return candidate;
        return 0;
    }

    private static bool IsExplorer(nint window)
    {
        Native.GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static IReadOnlyList<DisplayTarget> GetDisplays()
    {
        var displays = new List<DisplayTarget>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (Native.GetMonitorInfo(monitor, ref info))
                displays.Add(new(info.Device, info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return displays.OrderByDescending(d => d.Primary).ThenBy(d => d.Id).ToArray();
    }

    public void Dispose() { _output?.Dispose(); _parking.Dispose(); }

    private static class Native
    {
        internal delegate bool EnumWindowProc(nint window, nint parameter);
        internal delegate bool MonitorProc(nint monitor, nint dc, ref Rect rect, nint data);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            public uint Size; public Rect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [DllImport("user32")] internal static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
        [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32")] internal static extern nint GetParent(nint window);
        [DllImport("user32")] internal static extern nint SetParent(nint window, nint parent);
        [DllImport("user32")] internal static extern bool IsWindow(nint window);
        [DllImport("user32")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32")] internal static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
        [DllImport("user32")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    }
}
