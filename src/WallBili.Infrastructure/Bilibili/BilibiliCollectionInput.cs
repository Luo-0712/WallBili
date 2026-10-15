using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

public static class BilibiliCollectionInput
{
    public static CollectionSource Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host != "space.bilibili.com" || !string.IsNullOrEmpty(uri.UserInfo)) throw Invalid();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !long.TryParse(segments[0], out var uid) || uid <= 0) throw Invalid();
        var query = new Dictionary<string, string>();
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2) query[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
        }
        var kind = query.GetValueOrDefault("type", "season") switch
        {
            "season" => CollectionKind.Season, "series" => CollectionKind.Series, _ => throw Invalid()
        };
        string? id;
        if (segments.Length == 3 && segments[1] == "lists") id = segments[2];
        else if (segments.Length == 3 && segments[1] == "channel" && segments[2] is "collectiondetail" or "seriesdetail")
        {
            kind = segments[2] == "seriesdetail" ? CollectionKind.Series : CollectionKind.Season;
            id = query.GetValueOrDefault("sid");
        }
        else throw Invalid();
        if (!long.TryParse(id, out var collectionId) || collectionId <= 0) throw Invalid();
        return new(uid, collectionId, kind);
    }

    private static ContentAccessException Invalid() => new(ContentFailureKind.InvalidInput,
        "Provide a space.bilibili.com collection/series link, or enter its uploader and collection IDs.");
}
