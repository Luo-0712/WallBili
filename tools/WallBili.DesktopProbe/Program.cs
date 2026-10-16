using System.Diagnostics;
using System.Runtime.InteropServices;
using WallBili.Core;
using WallBili.Player.Mpv;

namespace WallBili.DesktopProbe;

/// <summary>Conservative local-video experiment: create only our own child under an existing Explorer wallpaper host.</summary>
internal static class Program
{
    private static readonly Native.WindowProc Procedure = WindowProc;
    private static nint _host, _parent;
    private static uint _parentPid;
    private static long _deadline;
    private static int _exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length is < 1 or > 2 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Windows only. Usage: WallBili.DesktopProbe LOCAL_VIDEO [SECONDS=30]");
            return 2;
        }
        var seconds = 30;
        if (args.Length == 2 && (!int.TryParse(args[1], out seconds) || seconds is < 1 or > 600)) return 2;
        using var lifetime = new CancellationTokenSource();
        Task playback = Task.CompletedTask;
        var className = "WallBili.DesktopProbe." + Guid.NewGuid().ToString("N");
        var instance = Native.GetModuleHandle(null);
        try
        {
            _parent = FindExistingHost();
            if (_parent == 0)
            {
                Console.Error.WriteLine("No existing Explorer wallpaper WorkerW was found. No shell windows were changed. Record this as an unsupported binding candidate.");
                return 3;
            }
            Native.GetWindowThreadProcessId(_parent, out _parentPid);
            var windowClass = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Procedure, Instance = instance, ClassName = className };
            if (Native.RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException();
            var origin = new Native.Point();
            Native.MapWindowPoints(0, _parent, ref origin, 1);
            // Child + no-activate + transparent: this window never becomes an input surface above desktop icons.
            _host = Native.CreateWindowEx(0x08000020, className, "WallBili local desktop probe", 0x50000000,
                origin.X, origin.Y, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1), _parent, 0, instance, 0);
            if (_host == 0 || Native.GetParent(_host) != _parent) throw new InvalidOperationException();
            _deadline = Environment.TickCount64 + seconds * 1000L;
            if (Native.SetTimer(_host, 1, 200, 0) == 0) throw new InvalidOperationException();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Native.PostMessage(_host, 0x0010, 0, 0); };
            var host = _host;
            var file = new Uri(Path.GetFullPath(args[0]));
            playback = Task.Run(async () =>
            {
                try
                {
                    await using var player = new MpvPlayer(new() { WindowId = host.ToInt64(), StartPaused = true });
                    await player.LoadAsync(new(new("local-probe", 1), new(file, "local", 0), null,
                        new Dictionary<string, string>(), DateTimeOffset.UtcNow, null), lifetime.Token);
                    await player.SetVolumeAsync(0, lifetime.Token);
                    await player.ResumeAsync(lifetime.Token);
                    Console.WriteLine("Local video attached to our own child HWND. Observe visible video, icon clicks and Wallpaper Engine before/during/after. Ctrl+C exits early.");
                    await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch
                {
                    _exitCode = 4;
                    Console.Error.WriteLine("Desktop playback failed; inspect the local video and MPV_PATH. No coexistence pass is claimed.");
                    Native.PostMessage(host, 0x0010, 0, 0);
                }
            });
            while (true)
            {
                var result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new InvalidOperationException();
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch
        {
            _exitCode = 4;
            Console.Error.WriteLine("Could not create the owned desktop host; no coexistence pass is claimed.");
        }
        finally
        {
            lifetime.Cancel();
            try { playback.GetAwaiter().GetResult(); } catch { }
            if (_host != 0 && Native.IsWindow(_host)) Native.DestroyWindow(_host);
            Native.UnregisterClass(className, instance);
            GC.KeepAlive(Procedure);
        }
        Console.WriteLine("Owned mpv process and host cleaned up. Human observations are required to assess Wallpaper Engine coexistence.");
        return _exitCode;
    }

    private static nint FindExistingHost()
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

    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0113) // timer: stop if the shell host disappears or changes owner
        {
            Native.GetWindowThreadProcessId(_parent, out var pid);
            if (Environment.TickCount64 >= _deadline || !Native.IsWindow(_parent) || pid != _parentPid)
                Native.DestroyWindow(window);
            return 0;
        }
        if (message == 0x0010) { Native.DestroyWindow(window); return 0; }
        if (message == 0x0002) { Native.PostQuitMessage(0); return 0; }
        if (message == 0x0084) return -1; // HTTRANSPARENT
        return Native.DefWindowProc(window, message, wParam, lParam);
    }
}

internal static class Native
{
    public delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    public delegate bool EnumWindowProc(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32")] public static extern nint GetParent(nint window);
    [DllImport("user32")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32")] public static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
    [DllImport("user32")] public static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32")] public static extern bool IsWindow(nint window);
    [DllImport("user32")] public static extern bool DestroyWindow(nint window);
    [DllImport("user32")] public static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32")] public static extern bool TranslateMessage(ref Message message);
    [DllImport("user32")] public static extern nint DispatchMessage(ref Message message);
    [DllImport("user32")] public static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
}
