using System.IO;
using System.Windows.Media;

namespace WallBili.Desktop;

/// <summary>设计稿（.codebuddy/figma/2_2）声明的字体栈：拉丁字形用随应用分发的 Inter，
/// 中文按设计稿顺序回退到思源黑体 / Noto Sans SC / 微软雅黑；WPF 的字族列表是逐字回退的，
/// 所以同一个 FontFamily 能同时给出"I 用 Inter、中文用思源"的效果。
///
/// 为什么是一个类型而不是在 XAML 里写字体名：WPF 加载未安装字体只认磁盘文件——
/// 把 ttf 嵌成 pack:// 资源会**静默**回退成默认字体（实测六种 URI 写法全部落到默认字体，
/// 见 tests/WallBili.Desktop.Tests/DesignFontTests 的说明），所以 Fonts 目录随输出目录分发
/// （见 csproj 的 Content 项），这里用它的文件 URI 构造。回归测试会把 App.xaml 的资源整体
/// 复制到隔离窗口里渲染，走同一个构造函数，因此截图与真实窗口用的是同一套字。</summary>
public sealed class DesignFont : FontFamily
{
    /// <summary>随输出分发的 Inter 字体文件（SIL OFL 1.1，见 Fonts/LICENSE-Inter.txt）。</summary>
    public const string InterRegularFile = "Fonts/Inter-Regular.ttf";
    public const string InterBoldFile = "Fonts/Inter-Bold.ttf";

    /// <summary>设计稿字体栈，首项 relative URI 相对输出目录解析（Inter 缺失时逐级回退）。</summary>
    private const string Stack = "./Fonts/#Inter, Geist Sans, Segoe UI Variable Text, Segoe UI, " +
        "Source Han Sans SC, 思源黑体, Noto Sans SC, Microsoft YaHei UI, Microsoft YaHei";

    /// <summary>设计稿里中文优先的字体，按顺序尝试；都不在时装的是哪套由系统决定。</summary>
    public static readonly string[] ChineseFamilies =
        ["Source Han Sans SC", "思源黑体", "Noto Sans SC", "Microsoft YaHei UI", "Microsoft YaHei"];

    public DesignFont() : base(OutputDirectory(), Stack)
    {
    }

    /// <summary>字体的相对 URI 要相对"目录"解析，基 URI 必须以分隔符结尾。</summary>
    private static Uri OutputDirectory()
    {
        var directory = AppContext.BaseDirectory;
        if (!directory.EndsWith(Path.DirectorySeparatorChar)) directory += Path.DirectorySeparatorChar;
        return new Uri(directory, UriKind.Absolute);
    }
}
