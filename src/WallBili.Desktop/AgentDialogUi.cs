using System.Windows;
using System.Windows.Controls;

namespace WallBili.Desktop;

internal static class AgentDialogUi
{
    public static void Style(FrameworkElement element, string key) => element.SetResourceReference(FrameworkElement.StyleProperty, key);
    public static TextBlock Text(string value, string style = "Text.Body")
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Style(text, style);
        return text;
    }
    public static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 10, 0), MinHeight = 34 };
        Style(button, primary ? "Btn.Primary" : "Btn.Ghost");
        button.Click += click;
        return button;
    }
    public static void Window(Window window, string title, double width, double height)
    {
        window.Title = title;
        window.Width = width; window.Height = height;
        window.MinWidth = width; window.MinHeight = Math.Min(height, 550);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.FontFamily = WallBili.Desktop.Presentation.UiTypography.Family;
        window.SetResourceReference(Control.BackgroundProperty, "Surface");
        window.SetResourceReference(Control.ForegroundProperty, "Ink");
    }
}
