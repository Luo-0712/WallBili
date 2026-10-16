using System.Globalization;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Public account identity as returned by the navigation endpoint. Contains no credentials.</summary>
public sealed record BilibiliAccount(long Uid, string Name, bool IsVip);

public sealed class BilibiliAccountChangedEventArgs(BilibiliAccount? account, int sessionVersion) : EventArgs
{
    public BilibiliAccount? Account { get; } = account;
    public int SessionVersion { get; } = sessionVersion;
}

/// <summary>A web QR login challenge; the QR image encodes <see cref="LoginUrl"/>.</summary>
public sealed record QrLoginChallenge(Uri LoginUrl, string QrcodeKey);

public enum QrLoginState
{
    WaitingScan,
    WaitingConfirm,
    Succeeded,
    Expired,
    /// <summary>The polled challenge is no longer the active one: a late poll cannot replace the session.</summary>
    Superseded
}

public sealed record QrLoginPoll(QrLoginState State, string? CookieText = null, BilibiliAccount? Account = null);

/// <summary> Parses standard <c>name=value; …</c> cookie text. Rejects line breaks and malformed pairs,
/// keeps the original value encoding and retains only the fields needed for signed-in and guest requests.</summary>
public static class BilibiliCookieText
{
    /// <summary>Covers account identity plus the device fields required for anonymous requests.</summary>
    private static readonly HashSet<string> RetainedNames = new(StringComparer.Ordinal)
    { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "buvid3", "buvid4" };

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
            throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "Cookie 输入无效：不能为空，也不能包含换行。");
        var pairs = new List<(string Name, string Value)>();
        foreach (var segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Trim();
            if (pair.Length == 0) continue;
            var separator = pair.IndexOf('=');
            if (separator < 1) throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "Cookie 输入无效：每一段都必须是 name=value 形式。");
            var name = pair[..separator];
            var value = pair[(separator + 1)..];
            if (name.Length is < 1 or > 64 || name.Any(c => c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9') or '_' or '.')))
                throw new ContentAccessException(ContentFailureKind.InvalidInput, "Cookie 输入无效：字段名不合法。");
            if (value.Length is < 1 or > 2048 || value.Any(c => c <= ' ' || c >= (char)127))
                throw new ContentAccessException(ContentFailureKind.InvalidInput, "Cookie 输入无效：字段值不合法。");
            pairs.Add((name, value));
        }
        var retained = pairs.Where(p => RetainedNames.Contains(p.Name)).ToList();
        if (retained.All(p => p.Name != "SESSDATA"))
            throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "Cookie 输入无效：至少需要包含 SESSDATA。");
        return string.Join("; ", retained.Select(p => p.Name + "=" + p.Value));
    }

    /// <summary>Builds a request cookie header from Set-Cookie response values, keeping the original encoding.</summary>
    public static string FromSetCookieValues(IEnumerable<string> setCookieValues)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (var cookie in setCookieValues)
        {
            var pair = cookie.Split(';', 2)[0].Trim();
            var separator = pair.IndexOf('=');
            if (separator < 1) continue;
            var name = pair[..separator];
            var value = pair[(separator + 1)..];
            if (name.Length is < 1 or > 64 || value.Length == 0 || value.Length > 2048) continue;
            if (!RetainedNames.Contains(name) || pairs.Any(p => p.Name == name)) continue;
            pairs.Add((name, value));
        }
        if (pairs.Count == 0)
            throw new ContentAccessException(ContentFailureKind.InvalidResponse,
                "The login response did not contain usable credentials.");
        return string.Join("; ", pairs.Select(p => p.Name + "=" + p.Value));
    }

    public static string? FindValue(string cookieText, string name)
    {
        foreach (var segment in cookieText.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Trim();
            var separator = pair.IndexOf('=');
            if (separator < 1) continue;
            if (string.Equals(pair[..separator], name, StringComparison.Ordinal))
                return pair[(separator + 1)..];
        }
        return null;
    }

    public static string Describe(BilibiliAccount account) =>
        account.Name + "（UID " + account.Uid.ToString(CultureInfo.InvariantCulture) + "）";
}
