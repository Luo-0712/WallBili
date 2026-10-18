using System.Windows;

namespace WallBili.Desktop;

public partial class App : Application
{
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
        _instance = new(false, @"Local\WallBili.Desktop.Library." + Options.InstanceId);
        _showSignal = new(false, EventResetMode.AutoReset, @"Local\WallBili.Desktop.Show." + Options.InstanceId);
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance) { _showSignal.Set(); Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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
        base.OnExit(e);
    }
}
