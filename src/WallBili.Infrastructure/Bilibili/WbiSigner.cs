using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Independent implementation of the public web WBI protocol; no account secret is involved.</summary>
internal static class WbiSigner
{
    // Protocol permutation, documented at https://sessionhu.github.io/bilibili-API-collect/docs/misc/sign/wbi.html
    private static readonly int[] Permutation =
    [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
     27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];

    internal static string Sign(IReadOnlyDictionary<string, string> parameters, string imgUrl, string subUrl,
        DateTimeOffset now)
    {
        var rawKey = ImageKey(imgUrl) + ImageKey(subUrl);
        if (rawKey.Length != 64)
            throw new ContentAccessException(ContentFailureKind.InvalidResponse, "The WBI signing keys were not usable.");
        var mixin = new string(Permutation.Select(index => rawKey[index]).ToArray());
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters) values[pair.Key] = pair.Value;
        values["wts"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var query = string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" +
            Uri.EscapeDataString(string.Concat(pair.Value.Where(c => c is not ('!' or '\'' or '(' or ')' or '*'))))));
        // MD5 is required by the upstream wire protocol; it is not used for security or password storage.
        var digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin)));
        return query + "&w_rid=" + digest;
    }

    private static string ImageKey(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? Path.GetFileNameWithoutExtension(uri.AbsolutePath) : "";
}
