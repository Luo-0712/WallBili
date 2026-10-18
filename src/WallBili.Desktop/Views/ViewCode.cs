using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WallBili.Desktop.Presentation;

namespace WallBili.Desktop.Views;

public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("添加第一个来源"));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public EmptyState() => InitializeComponent();
}
public partial class SourceList : UserControl
{
    public static readonly DependencyProperty ShowRemoveProperty = DependencyProperty.Register(nameof(ShowRemove), typeof(bool), typeof(SourceList), new PropertyMetadata(false));
    public bool ShowRemove { get => (bool)GetValue(ShowRemoveProperty); set => SetValue(ShowRemoveProperty, value); }
    public SourceList() => InitializeComponent();
}
public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => InitializeComponent();
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => ViewActions.ChangeMode(sender, DataContext);
}
public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();
    private void Videos_Checked(object sender, RoutedEventArgs e) { if (DataContext is ShellViewModel vm) vm.ShowSources = false; }
    private void Sources_Checked(object sender, RoutedEventArgs e) { if (DataContext is ShellViewModel vm) vm.ShowSources = true; }
    private void Catalog_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel vm && ViewActions.ClickedRow(e.OriginalSource) is VideoRow row) vm.PlayVideoCommand.Execute(row);
    }
}
public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => ViewActions.ChangeMode(sender, DataContext);
    private void Queue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel vm && ViewActions.ClickedRow(e.OriginalSource) is QueueRow row) vm.PlayQueueCommand.Execute(row);
    }
}
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void Fill_Checked(object sender, RoutedEventArgs e)
    { if (DataContext is ShellViewModel vm && sender is RadioButton radio) vm.FillWindow = radio.Tag as string == "fill"; }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 mpv 播放器", Filter = "mpv 播放器|mpv.exe|可执行程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && DataContext is ShellViewModel vm) vm.MpvPath = dialog.FileName;
    }
}
public partial class ImportView : UserControl
{
    public ImportView() => InitializeComponent();
    internal void FocusInput() { if (DataContext is ShellViewModel { ImportIds: true }) ImportUid.Focus(); else ImportLink.Focus(); }
}
internal static class ViewActions
{
    internal static void ChangeMode(object sender, object context)
    {
        if (context is ShellViewModel vm && sender is ComboBox { SelectedValue: string id } && id.Length > 0 && id != vm.ModeId)
            vm.ChangeModeCommand.Execute(id);
    }
    internal static object? ClickedRow(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return null;
            if (current is DataGridRow row) return row.Item;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
