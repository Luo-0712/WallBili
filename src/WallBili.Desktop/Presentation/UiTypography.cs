using System.Windows.Markup;
using System.Windows.Media;

namespace WallBili.Desktop.Presentation;

/// <summary>Route scripts explicitly: MiSans contains Han glyphs too, so a fallback list alone is insufficient.</summary>
public static class UiTypography
{
    public static string HanFamily { get; } = Pick("Noto Sans SC", "Source Han Sans SC", "Source Han Sans CN", "Microsoft YaHei UI");
    public static string LatinFamily { get; } = Pick("MiSans", "Segoe UI");
    public static FontFamily Family { get; } = Create();

    private static string Pick(params string[] candidates)
    {
        var installed = Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values.Append(f.Source))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(installed.Contains) ?? candidates[^1];
    }

    private static FontFamily Create()
    {
        var family = new FontFamily();
        family.FamilyNames.Add(XmlLanguage.GetLanguage("en-US"), "WallBili UI");
        family.FamilyMaps.Add(new() { Unicode = "2E80-A4CF,AC00-D7AF,F900-FAFF,FE10-FE6F,FF00-FFEF,20000-323AF", Target = HanFamily });
        family.FamilyMaps.Add(new() { Unicode = "0000-024F,1E00-1EFF,2000-206F", Target = LatinFamily });
        family.FamilyMaps.Add(new() { Target = $"{HanFamily}, {LatinFamily}, Segoe UI Emoji, Segoe UI Symbol" });
        return family;
    }
}
