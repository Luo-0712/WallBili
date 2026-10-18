using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Public web discovery using the captured visitor/account session. Never resolves signed media URLs.</summary>
public sealed partial class BilibiliClient : IContentSearchClient
{
    public async Task<ContentSearchPage> SearchAsync(string query, string kind = "video", int page = 1,
        int pageSize = 20, string order = "relevance", CancellationToken cancellationToken = default)
    {
        ValidateDiscoveryPaging(page, pageSize);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl))
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "A search query of 1–200 characters is required.");
        var creatorSearch = kind is "creator" or "user" or "bili_user";
        if (!creatorSearch && kind != "video")
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "Search kind must be video or creator.");
        var upstreamOrder = creatorSearch ? order switch
        {
            "relevance" or "default" => "",
            "fans" or "level" => order,
            _ => throw new ContentAccessException(ContentFailureKind.InvalidInput, "Creator search order must be relevance, fans or level.")
        } : order switch
        {
            "relevance" or "default" => "totalrank",
            "totalrank" or "click" or "pubdate" or "dm" or "stow" or "scores" => order,
            _ => throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "Video search order must be relevance, click, pubdate, dm, stow or scores.")
        };
        var session = CaptureSearchSession(cancellationToken);
        var data = await GetSearchDataAsync("/x/web-interface/wbi/search/type", new Dictionary<string, string>
        {
            ["keyword"] = query.Trim(), ["search_type"] = creatorSearch ? "bili_user" : "video",
            ["page"] = Invariant(page), ["page_size"] = Invariant(pageSize), ["order"] = upstreamOrder,
            ["order_sort"] = "0"
        }, session, signed: true, cancellationToken);
        var returnedPage = RequiredNonnegativeInt(data, "page");
        var returnedSize = RequiredNonnegativeInt(data, "pagesize");
        var total = RequiredNonnegativeInt(data, "numResults");
        ValidateSearchPage(returnedPage, returnedSize, page, pageSize);
        var list = Property(data, "result");
        ValidateDiscoveryList(list, page, returnedSize, total);
        var items = new List<ContentSearchItem>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            // Video search also inserts promoted paid courses (type=ketang) without a BV identity.
            // Keep upstream pagination so filtering these slots cannot skip the following page.
            if (!creatorSearch && Text(item, "type") is { Length: > 0 } resultType && resultType != "video") continue;
            var uid = Number(item, "mid");
            if (uid <= 0) throw InvalidResponse("A search result contained an invalid creator identity.");
            if (creatorSearch)
            {
                var name = SearchPlainText(Text(item, "uname"));
                if (name.Length == 0 || !identities.Add(Invariant(uid)))
                    throw InvalidResponse("The search result contained an invalid or duplicate creator.");
                items.Add(new(new("creator", UploaderId: uid), name, name, uid));
            }
            else
            {
                var bvid = Text(item, "bvid");
                if (!BilibiliInput.IsBvid(bvid) || !identities.Add(bvid))
                    throw InvalidResponse("The search result contained an invalid or duplicate video identity.");
                items.Add(SearchVideo(item, bvid, uid, "pubdate", "duration"));
            }
        }
        ValidateSearchSession(session, cancellationToken);
        return new(returnedPage, returnedSize, total, items);
    }

    public async Task<ContentSearchPage> GetCreatorContentAsync(long uploaderId, string kind = "videos",
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        ValidateDiscoveryPaging(page, pageSize);
        if (uploaderId <= 0 || kind is not ("videos" or "collections"))
            throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "A positive creator identity and videos or collections kind are required.");
        var session = CaptureSearchSession(cancellationToken);
        ContentSearchPage result;
        if (kind == "videos")
        {
            var data = await GetSearchDataAsync("/x/space/wbi/arc/search", new Dictionary<string, string>
            {
                ["mid"] = Invariant(uploaderId), ["pn"] = Invariant(page), ["ps"] = Invariant(pageSize), ["order"] = "pubdate"
            }, session, signed: true, cancellationToken);
            var pagination = Property(data, "page");
            var returnedSize = RequiredNonnegativeInt(pagination, "ps");
            ValidateSearchPage(RequiredNonnegativeInt(pagination, "pn"), returnedSize, page, pageSize);
            var total = RequiredNonnegativeInt(pagination, "count");
            var list = Property(Property(data, "list"), "vlist");
            ValidateDiscoveryList(list, page, returnedSize, total);
            var items = new List<ContentSearchItem>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                var bvid = Text(item, "bvid");
                if (!BilibiliInput.IsBvid(bvid) || Number(item, "mid") != uploaderId || !identities.Add(bvid))
                    throw InvalidResponse("A creator video contained an unexpected or duplicate identity.");
                items.Add(SearchVideo(item, bvid, uploaderId, "created", "length"));
            }
            result = new(page, returnedSize, total, items);
        }
        else result = await GetCreatorCollectionsAsync(uploaderId, page, pageSize, session, cancellationToken);
        ValidateSearchSession(session, cancellationToken);
        return result;
    }

    private async Task<ContentSearchPage> GetCreatorCollectionsAsync(long uploaderId, int page, int pageSize,
        SearchSession session, CancellationToken ct)
    {
        var data = await GetSearchDataAsync("/x/polymer/web-space/seasons_series_list", new Dictionary<string, string>
        {
            ["mid"] = Invariant(uploaderId), ["page_num"] = Invariant(page), ["page_size"] = Invariant(pageSize)
        }, session, signed: false, ct);
        var lists = Property(data, "items_lists");
        var pagination = Property(lists, "page");
        var returnedSize = RequiredNonnegativeInt(pagination, "page_size");
        ValidateSearchPage(RequiredNonnegativeInt(pagination, "page_num"), returnedSize, page, pageSize);
        var total = RequiredNonnegativeInt(pagination, "total");
        var items = new List<ContentSearchItem>();
        var identities = new HashSet<CollectionSource>();
        foreach (var (field, idField, kind) in new[]
        {
            ("seasons_list", "season_id", CollectionKind.Season), ("series_list", "series_id", CollectionKind.Series)
        })
        {
            var list = Property(lists, field);
            if (list.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("The creator collection list was missing.");
            foreach (var item in list.EnumerateArray())
            {
                var meta = Property(item, "meta");
                var identity = new CollectionSource(uploaderId, Number(meta, idField), kind);
                var title = SearchPlainText(Text(meta, "name"));
                if (Number(meta, "mid") != uploaderId || identity.CollectionId <= 0 || title.Length == 0 || !identities.Add(identity))
                    throw InvalidResponse("A creator collection contained an unexpected, invalid or duplicate identity.");
                items.Add(new(new("collection", UploaderId: uploaderId, CollectionId: identity.CollectionId,
                    CollectionKind: kind), title, UploaderId: uploaderId));
            }
        }
        if (items.Count > returnedSize || items.Count > total || items.Count == 0 && (long)(page - 1) * returnedSize < total)
            throw InvalidResponse("The creator collection response contained incomplete pagination.");
        return new(page, returnedSize, total, items);
    }

    private static ContentSearchItem SearchVideo(JsonElement item, string bvid, long uid, string publishedField, string durationField)
    {
        var title = SearchPlainText(Text(item, "title"));
        var author = SearchPlainText(Text(item, "author"));
        var published = Number(item, publishedField);
        if (title.Length == 0 || author.Length == 0 || published <= 0 || published > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw InvalidResponse("The search result contained an invalid title, creator or publication date.");
        return new(new("video", Bvid: bvid), title, author, uid, ParseDiscoveryDuration(Text(item, durationField)),
            DateTimeOffset.FromUnixTimeSeconds(published));
    }

    private static void ValidateSearchPage(int returnedPage, int returnedSize, int page, int requestedSize)
    {
        if (returnedPage != page || returnedSize < 1 || returnedSize > requestedSize)
            throw InvalidResponse("The content search response did not match the requested pagination.");
    }

    private static string Invariant(long number) => number.ToString(CultureInfo.InvariantCulture);
    private static string SearchPlainText(string text) => SearchMarkup().Replace(WebUtility.HtmlDecode(text), "").Trim();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SearchMarkup();

    private static IReadOnlyList<CollectionSource>? ReadRelatedCollections(JsonElement video)
    {
        var season = Property(video, "ugc_season");
        var id = Number(season, "id");
        var uploader = Number(season, "mid");
        if (uploader <= 0) uploader = Number(Property(video, "owner"), "mid");
        return id > 0 && uploader > 0 ? [new(uploader, id)] : null;
    }

    // Captured credentials deliberately have no generated ToString diagnostics.
    private sealed class SearchSession(int version, long? uid, string? cookies)
    {
        public int Version { get; } = version;
        public long? Uid { get; } = uid;
        public string? Cookies { get; set; } = cookies;
    }

    private SearchSession CaptureSearchSession(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_accountGate) return new(_sessionVersion, _account?.Uid, _accountCookies);
    }

    private void ValidateSearchSession(SearchSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_accountGate)
            if (_sessionVersion != session.Version || _account?.Uid != session.Uid)
                throw new OperationCanceledException("The content discovery account session was superseded.", ct);
    }

    private async Task<JsonElement> GetSearchDataAsync(string path, Dictionary<string, string> parameters,
        SearchSession session, bool signed, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var material = await EnsureSearchSessionAsync(session, signed, forceRefresh: attempt > 0, ct);
                var query = signed
                    ? WbiSigner.Sign(parameters, material!.ImageUrl, material.SubUrl, DateTimeOffset.UtcNow)
                    : string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
                ValidateSearchSession(session, ct);
                var data = Data(await GetJsonAsync(Api + path + "?" + query, ct, cookieOverride: session.Cookies));
                ValidateSearchSession(session, ct);
                return data;
            }
            catch (ContentAccessException error) when (signed && attempt == 0 && error.ServiceCode == -403)
            {
                ValidateSearchSession(session, ct);
                // Retry a rotated WBI key once. Risk control and entitlement failures stay classified.
            }
            catch
            {
                ValidateSearchSession(session, ct);
                throw;
            }
        }
    }

    private async Task<WbiMaterial?> EnsureSearchSessionAsync(SearchSession session, bool needWbi, bool forceRefresh, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            ValidateSearchSession(session, ct);
            if (session.Uid is null)
            {
                if (_visitorCookies is null)
                {
                    var visitor = Data(await GetJsonAsync(Api + "/x/frontend/finger/spi", ct));
                    var buvid3 = Text(visitor, "b_3");
                    var buvid4 = Text(visitor, "b_4");
                    if (!IsCookieValue(buvid3) || !IsCookieValue(buvid4))
                        throw InvalidResponse("The anonymous visitor credentials were missing or invalid.");
                    ValidateSearchSession(session, ct);
                    _visitorCookies = "buvid3=" + buvid3 + "; buvid4=" + buvid4;
                }
                session.Cookies = _visitorCookies;
            }
            if (!needWbi) return null;
            lock (_accountGate)
            {
                ValidateSearchSession(session, ct);
                if (!forceRefresh && _wbi is { } cached && DateTimeOffset.UtcNow < cached.RefreshAt) return cached;
            }
            var nav = Data(await GetJsonAsync(Api + "/x/web-interface/nav", ct,
                acceptAnonymousNav: session.Uid is null, cookieOverride: session.Cookies));
            ValidateSearchSession(session, ct);
            if (session.Uid is { } uid && (Number(nav, "mid") != uid ||
                Property(nav, "isLogin").ValueKind != JsonValueKind.True && Number(nav, "isLogin") != 1))
                throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "The content discovery account session has expired.");
            var keys = Property(nav, "wbi_img");
            var material = new WbiMaterial(Text(keys, "img_url"), Text(keys, "sub_url"), DateTimeOffset.UtcNow.AddHours(12));
            _ = WbiSigner.Sign(new Dictionary<string, string>(), material.ImageUrl, material.SubUrl, DateTimeOffset.UtcNow);
            lock (_accountGate)
            {
                ValidateSearchSession(session, ct);
                _wbi = material;
            }
            return material;
        }
        finally { _sessionGate.Release(); }
    }
}
