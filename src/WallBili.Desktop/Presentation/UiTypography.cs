using System.IO;
using System.Windows.Markup;
using System.Windows.Media;

namespace WallBili.Desktop.Presentation;

/// <summary>Private, distributed faces with explicit script and role selection, independent of Windows font installation.</summary>
public static class UiTypography
{
    public static string HanFamily { get; } = Reference("NotoSansSC-VF.ttf", "Noto Sans SC");
    public static string LatinFamily { get; } = Reference("MiSansLatin-Regular.otf", "MiSans Latin");
    public static FontFamily Family { get; } = Create(LatinFamily, "WallBili UI");
    public static FontFamily EmphasisFamily { get; } = Create(Reference("MiSansLatin-Demibold.otf", "MiSans Latin"), "WallBili UI Medium");
    public static FontFamily StrongFamily { get; } = Create(Reference("MiSansLatin-Semibold.otf", "MiSans Latin"), "WallBili UI Heading");

    private static string Reference(string file, string family)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", file);
        if (!File.Exists(path)) throw new FileNotFoundException("随包字体缺失，请保留程序目录中的完整 Fonts 文件夹。", path);
        // A file reference, rather than a directory, prevents 400 from matching MiSans's 450 face.
        return new Uri(path).AbsoluteUri + "#" + family;
    }

    private static FontFamily Create(string latin, string name)
    {
        var family = new FontFamily();
        family.FamilyNames.Add(XmlLanguage.GetLanguage("en-US"), name);
        family.FamilyMaps.Add(new() { Unicode = "2E80-A4CF,AC00-D7AF,F900-FAFF,FE10-FE6F,FF00-FFEF,20000-323AF", Target = HanFamily });
        family.FamilyMaps.Add(new() { Unicode = "0000-024F,1E00-1EFF,2000-206F", Target = latin });
        family.FamilyMaps.Add(new() { Target = $"{HanFamily}, {latin}, Segoe UI Emoji, Segoe UI Symbol" });
        return family;
    }
}
