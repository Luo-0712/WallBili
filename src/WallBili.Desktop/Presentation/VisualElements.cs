using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WallBili.Desktop.Presentation;

public sealed class LineIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(LineIcon), new FrameworkPropertyMetadata("Music", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LineIcon), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["Sound"] = Shape("M4,10 L4,14 M8,6 L8,18 M12,3 L12,21 M16,7 L16,17 M20,10 L20,14"),
        ["Home"] = Shape("M3,10 L12,3 21,10 21,20 15,20 15,14 9,14 9,20 3,20 Z"),
        ["Library"] = Shape("M4,4 L20,4 20,20 4,20 Z M4,10 L20,10 M10,10 L10,20"),
        ["Queue"] = Shape("M4,6 L20,6 M4,12 L15,12 M4,18 L11,18 M18,13 L18,21 M15,18 L18,21 21,18"),
        ["Monitor"] = Shape("M3,4 L21,4 21,17 3,17 Z M8,21 L16,21 M12,17 L12,21"),
        ["Settings"] = Shape("M9,3 L15,3 16,6 19,7 21,12 19,17 16,18 15,21 9,21 8,18 5,17 3,12 5,7 8,6 Z M15,12 A3,3 0 1 0 9,12 A3,3 0 1 0 15,12"),
        ["Plus"] = Shape("M12,5 L12,19 M5,12 L19,12"),
        ["Close"] = Shape("M6,6 L18,18 M6,18 L18,6"),
        ["Down"] = Shape("M12,5 L12,19 M7,14 L12,19 17,14"),
        ["Up"] = Shape("M12,19 L12,5 M7,10 L12,5 17,10"),
        ["Chevron"] = Shape("M8,10 L12,14 16,10"),
        ["Arrow"] = Shape("M4,12 L20,12 M15,7 L20,12 15,17"),
        ["Search"] = Shape("M17,10.5 A6.5,6.5 0 1 0 4,10.5 A6.5,6.5 0 1 0 17,10.5 M15.5,15.5 L21,21"),
        ["Play"] = Shape("M8,5 L19,12 8,19 Z"),
        ["Pause"] = Shape("M8,6 L8,18 M16,6 L16,18"),
        ["Next"] = Shape("M6,5 L16,12 6,19 Z M19,5 L19,19"),
        ["Previous"] = Shape("M18,5 L8,12 18,19 Z M5,5 L5,19"),
        ["Stop"] = Shape("M6,6 L18,6 18,18 6,18 Z"),
        ["Refresh"] = Shape("M19,9 A7.5,7.5 0 1 0 19.1,16 M19,3 L19,9 13,9"),
        ["Volume"] = Shape("M4,9 L8,9 13,5 13,19 8,15 4,15 Z M17,8 Q22,12 17,16 M20,5 Q28,12 20,19"),
        ["Check"] = Shape("M5,12 L9,16 19,6"),
        ["Tray"] = Shape("M4,14 L4,20 20,20 20,14 M12,3 L12,15 M8,11 L12,15 16,11"),
        ["Repeat"] = Shape("M5,7 L18,7 15,4 M18,7 L15,10 M19,17 L6,17 9,20 M6,17 L9,14 M3,14 L3,9 Q3,7 5,7 M21,10 L21,15 Q21,17 19,17"),
        ["Music"] = Shape("M9,18 L9,5 19,3 19,16 M9,7 L19,5 M9,18 A3,2 0 1 0 3,18 A3,2 0 1 0 9,18 M19,16 A3,2 0 1 0 13,16 A3,2 0 1 0 19,16"),
        ["Info"] = Shape("M21,12 A9,9 0 1 0 3,12 A9,9 0 1 0 21,12 M12,11 L12,17 M12,7 L12,8"),
        ["Headphones"] = Shape("M4,14 L4,11 A8,8 0 0 1 20,11 L20,14 M4,12 L8,12 8,20 4,20 Z M20,12 L16,12 16,20 20,20 Z"),
        ["Minimize"] = Shape("M5,12 L19,12"),
        ["Maximize"] = Shape("M5,5 L19,5 19,19 5,19 Z"),
        ["Link"] = Shape("M10,8 L13,5 A4,4 0 0 1 19,11 L16,14 M14,16 L11,19 A4,4 0 0 1 5,13 L8,10 M9,15 L15,9")
    };
    private static Geometry Shape(string value) { var geometry = Geometry.Parse(value); geometry.Freeze(); return geometry; }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 18 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 18 : availableSize.Height);
    protected override void OnRender(DrawingContext context)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        context.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, Kind == "Pause" ? 2.6 : 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.DrawGeometry(Kind is "Play" or "Next" or "Previous" ? Foreground : null, pen, shape);
        context.Pop(); context.Pop();
    }
}

public sealed class LandscapeArt : FrameworkElement
{
    private static SolidColorBrush Gray(byte value) => new(Color.FromRgb(value, value, value));
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 320, ActualHeight / 180));
        dc.DrawRectangle(Gray(244), null, new(0, 0, 320, 180));
        dc.DrawRectangle(Gray(232), null, new(37, 20, 175, 136));
        dc.DrawRectangle(Gray(249), null, new(41, 24, 167, 128));
        dc.DrawRectangle(Gray(242), null, new(46, 29, 157, 118));
        dc.DrawEllipse(Brushes.White, null, new(159, 59), 17, 17);
        dc.DrawGeometry(Gray(232), null, Geometry.Parse("M46,108 Q82,98 124,111 Q166,126 203,110 L203,147 46,147 Z"));
        dc.DrawGeometry(Gray(220), null, Geometry.Parse("M46,126 Q91,119 135,131 Q168,140 203,130 L203,147 46,147 Z"));
        dc.DrawRectangle(Brushes.White, null, new(118, 27, 4, 122));
        dc.DrawRectangle(Brushes.White, null, new(44, 92, 161, 4));
        dc.DrawGeometry(Gray(235), null, Geometry.Parse("M12,16 L53,16 Q39,85 53,160 L12,160 Z"));
        dc.DrawLine(new(Gray(222), .8), new(26, 17), new(20, 159));
        dc.DrawLine(new(Gray(222), .8), new(40, 17), new(35, 159));
        dc.DrawLine(new(Gray(207), 2), new(262, 88), new(262, 142));
        foreach (var path in new[] { "M262,110 Q239,112 241,96 Q259,95 262,110 Z", "M262,100 Q285,100 284,84 Q264,85 262,100 Z", "M262,89 Q244,86 249,72 Q265,76 262,89 Z", "M262,126 Q281,130 285,113 Q268,112 262,126 Z" })
            dc.DrawGeometry(Gray(208), null, Geometry.Parse(path));
        dc.DrawGeometry(Gray(216), null, Geometry.Parse("M249,138 L276,138 270,157 255,157 Z"));
        dc.DrawRectangle(Gray(212), null, new(242, 157, 42, 4));
        dc.DrawRectangle(Gray(218), null, new(246, 161, 2, 19));
        dc.DrawRectangle(Gray(218), null, new(279, 161, 2, 19));
        dc.DrawRectangle(Gray(237), null, new(0, 170, 320, 10));
        dc.Pop();
    }
}
