using System.Windows;

namespace WallBili.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private EventWaitHandle? _showSignal;
    private readonly CancellationTokenSource _signals = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(false, @"Local\WallBili.Desktop.Library");
        _showSignal = new(false, EventResetMode.AutoReset, @"Local\WallBili.Desktop.Show");
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                while (WaitHandle.WaitAny([_showSignal, _signals.Token.WaitHandle]) == 0)
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is not { } window) return;
                        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
                    });
            }
            catch (ObjectDisposedException) { }
        });
        base.OnStartup(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.ExitForSession();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signals.Cancel();
        _showSignal?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
