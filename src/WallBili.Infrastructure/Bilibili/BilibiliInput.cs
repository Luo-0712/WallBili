using System.Globalization;
using System.Text.RegularExpressions;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

public sealed record VideoInput(string Bvid, int Page = 1);

public static partial class BilibiliInput
{
    public static VideoInput Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw Invalid();
        input = input.Trim();
        if (BvidPattern().IsMatch(input))
            return new(input);
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw Invalid();
        if (uri.Host is not ("www.bilibili.com" or "bilibili.com" or "m.bilibili.com"))
            throw new ContentAccessException(ContentFailureKind.Unsupported, "Only Bilibili video links and BVIDs are supported.");
        var match = VideoPathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
            throw Invalid();
        var page = 1;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != "p") continue;
            if (parts.Length != 2 || !int.TryParse(Uri.UnescapeDataString(parts[1]), NumberStyles.None,
                    CultureInfo.InvariantCulture, out page) || page < 1)
                throw Invalid();
        }
        return new(match.Groups[1].Value, page);
    }

    internal static bool IsBvid(string? value) => value is not null && BvidPattern().IsMatch(value);

    private static ContentAccessException Invalid() => new(ContentFailureKind.InvalidInput,
        "Provide a BVID or a Bilibili /video/BV… link with a positive part number.");

    [GeneratedRegex("^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex BvidPattern();

    [GeneratedRegex("^/video/(BV[0-9A-Za-z]{10})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoPathPattern();
}
