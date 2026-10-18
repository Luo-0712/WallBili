using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WallBili.Desktop.Presentation;
using WallBili.Desktop.Views;

namespace WallBili.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopSession? _session;
    private readonly ShellViewModel _viewModel;
    private TrayIcon? _tray;
    private bool _forceExit, _closing, _allowClose;
    public MainWindow() : this(new DesktopSession(App.Options)) { }
    private MainWindow(DesktopSession session) : this(session.ViewModel) => _session = session;
    internal MainWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModelChanged;
        _viewModel.LoginRequested += Login;
        _viewModel.SignOutRequested += SignOut;
        PlaybackSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitSeek()), true);
        VolumeSlider.PreviewMouseLeftButtonDown += (_, _) => _viewModel.IsAdjustingVolume = true;
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitVolume()), true);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (!App.Options.Preview)
            _tray = new(Dispatcher, ShowFromTray, () => _viewModel.PlayPauseCommand.Execute(null), () => _viewModel.NextCommand.Execute(null),
                () => _viewModel.ToggleWallpaperCommand.Execute(null), ExitForSession);
        await _session.InitializeAsync();
        if (App.Options.Background && !App.Options.Preview) Hide();
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentTitle)) _tray?.SetStatus(_viewModel.CurrentTitle);
        if (e.PropertyName == nameof(ShellViewModel.IsImportOpen) && _viewModel.IsImportOpen && IsActive)
            Dispatcher.BeginInvoke(() => { FindVisual<ImportView>(ImportOverlay)?.FocusInput(); });
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Hide_Click(object sender, RoutedEventArgs e) { if (_tray is null) WindowState = WindowState.Minimized; else Hide(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    internal void ExitForSession() { _forceExit = true; Close(); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_session is null || _allowClose) return;
        e.Cancel = true;
        if (!_forceExit && _tray is not null) { Hide(); return; }
        if (_closing) return;
        _closing = true;
        try { await _session.DisposeAsync(); }
        finally
        {
            _tray?.Dispose();
            _viewModel.PropertyChanged -= ViewModelChanged;
            _viewModel.LoginRequested -= Login; _viewModel.SignOutRequested -= SignOut;
            _allowClose = true;
            Close();
            Application.Current?.Shutdown();
        }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsImportOpen) { _viewModel.CloseImportCommand.Execute(null); e.Handled = true; }
    }
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBoxBase or ComboBox) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void Seek_Down(object sender, MouseButtonEventArgs e) => _viewModel.IsSeeking = true;
    private void Seek_Up(object sender, MouseButtonEventArgs e) => CommitSeek();
    private void Seek_KeyUp(object sender, KeyEventArgs e) => CommitSeek();
    private void CommitSeek() { if (_viewModel.HasCurrent) _viewModel.SeekCommand.Execute(null); else _viewModel.IsSeeking = false; }
    private void CommitVolume() { _viewModel.IsAdjustingVolume = false; _viewModel.VolumeCommand.Execute(null); }
    private void Volume_Commit(object sender, MouseButtonEventArgs e) => CommitVolume();
    private void Volume_KeyUp(object sender, KeyEventArgs e) => CommitVolume();
    private async void Login()
    {
        if (_session?.Client is null) { _viewModel.Report("预览中使用示例内容。正常启动后可以登录 B 站账号。"); return; }
        var dialog = new LoginDialog(_session.Client) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.CookieResult is { } cookies)
            await _viewModel.RunAsync(() => { _session.SaveLogin(cookies); return Task.CompletedTask; });
    }
    private async void SignOut() { if (_session is not null) await _viewModel.RunAsync(() => _session.SignOutAsync()); }
}
