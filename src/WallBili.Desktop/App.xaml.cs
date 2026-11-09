using System.Runtime.InteropServices;
using System.Windows;

namespace WallBili.Desktop;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    private static int UiNativeThreadId() => GetCurrentThreadId();
    private Mutex? _instance;
    private bool _ownsInstance;
    private EventWaitHandle? _showSignal;
    private readonly CancellationTokenSource _signals = new();
    internal static StartupOptions Options { get; private set; } = StartupOptions.Parse([]);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { Options = StartupOptions.Parse(e.Args); }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, "WallBili"); Shutdown(2); return; }
        var instanceSuffix = Options.IsIsolated ? "." + Options.InstanceId : "";
        WallBili.Core.PerfLog.Start(Options.DataDirectory);
        _instance = new(false, @"Local\WallBili.Desktop.Library" + instanceSuffix);
        _showSignal = new(false, EventResetMode.AutoReset, @"Local\WallBili.Desktop.Show" + instanceSuffix);
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance)
        {
            WallBili.Core.PerfLog.Write("instance", "secondary instance detected, signalling primary and exiting");
            _showSignal.Set();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        // Applying visibility after Show returns is the only reliable place: Loaded can run while
        // Show is still applying its own Visible state. Session initialization continues hidden.
        if (Options.Background && !Options.Preview) window.Hide();
        StartUiStallMonitor(UiNativeThreadId());
        _ = Task.Run(() =>
        {
            try
            {
                while (WaitHandle.WaitAny([_showSignal, _signals.Token.WaitHandle]) == 0)
                    Dispatcher.BeginInvoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            }
            catch (ObjectDisposedException) { }
        });
    }
    /// <summary>Perf instrumentation: samples dispatcher responsiveness. The probe is posted, then
    /// polled — awaiting the operation would hang forever when the UI thread stops pumping, and the
    /// hang itself is what we need to see. Each sample also reports how much CPU the UI thread
    /// actually burned, which separates "busy computing" from "blocked waiting on another process".</summary>
    private void StartUiStallMonitor(int nativeId)
    {
        var dispatcher = Dispatcher;
        var uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        System.Diagnostics.ProcessThread? uiThread = null;
        try
        {
            foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
                if (t.Id == nativeId) { uiThread = t; break; }
        }
        catch { }
        _ = Task.Run(async () =>
        {
            TimeSpan lastCpu = default;
            var haveLast = false;
            while (true)
            {
                await Task.Delay(200).ConfigureAwait(false);
                double cpuDeltaMs = -1;
                if (uiThread is not null)
                {
                    try
                    {
                        var now = uiThread.TotalProcessorTime;
                        if (haveLast) cpuDeltaMs = (now - lastCpu).TotalMilliseconds;
                        lastCpu = now; haveLast = true;
                    }
                    catch { }
                }
                var completed = 0;
                var posted = System.Diagnostics.Stopwatch.GetTimestamp();
                dispatcher.BeginInvoke(() => System.Threading.Interlocked.Exchange(ref completed, 1),
                    System.Windows.Threading.DispatcherPriority.Background);
                double latencyMs = -1;
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (System.Threading.Volatile.Read(ref completed) != 0)
                    {
                        latencyMs = (System.Diagnostics.Stopwatch.GetTimestamp() - posted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        break;
                    }
                }
                var cpuField = cpuDeltaMs < 0 ? "ui_cpu_ms=n/a" : FormattableString.Invariant($"ui_cpu_ms={cpuDeltaMs:F0}");
                if (latencyMs < 0)
                    WallBili.Core.PerfLog.Write("ui-blocked", $"probe_unprocessed_2000ms {cpuField} ui_thread={uiThreadId}");
                else if (latencyMs > 100)
                    WallBili.Core.PerfLog.Write("ui-stall", FormattableString.Invariant($"latency_ms={latencyMs:F0} {cpuField}"));
            }
        });
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.ExitForSession();
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _signals.Cancel(); _showSignal?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose(); _signals.Dispose();
        try { WallBili.Core.PerfLog.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
