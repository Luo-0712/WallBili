using System.Globalization;
using System.Net;
using System.Text.Json;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Anonymous native WBI adapter. Visitor credentials and signing material stay in this instance.</summary>
public sealed class BilibiliClient(HttpClient httpClient, BilibiliClientOptions? options = null) : IContentClient
{
    private const string Api = "https://api.bilibili.com";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private const string Referer = "https://www.bilibili.com/";
    private readonly BilibiliClientOptions _options = options ?? new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private string? _visitorCookies;
    private WbiMaterial? _wbi;
    private sealed record WbiMaterial(string ImageUrl, string SubUrl, DateTimeOffset RefreshAt);

    public async Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default)
    {
        var parsed = BilibiliInput.Parse(input);
        var video = await GetSignedDataAsync("/x/web-interface/wbi/view",
            new Dictionary<string, string> { ["bvid"] = parsed.Bvid }, cancellationToken);

        if (Text(video, "bvid") is { Length: > 0 } returned && returned != parsed.Bvid)
            throw InvalidResponse("The video response did not match the requested BVID.");
        var title = Text(video, "title");
        var owner = Property(video, "owner");
        if (string.IsNullOrWhiteSpace(title)) throw InvalidResponse("The video title was missing.");
        // Signed metadata contains the complete authoritative per-part CID list.
        var partData = Property(video, "pages");
        if (partData.ValueKind != JsonValueKind.Array) throw InvalidResponse("The video part list was missing.");
        var parts = new List<VideoPart>();
        foreach (var part in partData.EnumerateArray())
        {
            var page = Int(part, "page");
            var cid = Number(part, "cid");
            if (page < 1 || cid < 1 || parts.Any(p => p.Page == page || p.Key.Cid == cid))
                throw InvalidResponse("The video part list contained invalid identities.");
            parts.Add(new(page, new(parsed.Bvid, cid), string.IsNullOrWhiteSpace(Text(part, "part")) ? title : Text(part, "part"), Seconds(part, "duration")));
        }
        if (parts.Count == 0) throw new ContentAccessException(ContentFailureKind.Removed, "This video has no available parts.");
        if (!parts.Any(p => p.Page == parsed.Page))
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "The selected video part does not exist.");
        return new(parsed.Bvid, title, Text(owner, "name"), Number(owner, "mid"), Seconds(video, "duration"),
            parts.OrderBy(p => p.Page).ToArray());
    }

    public async Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default)
    {
        if (key is null || !BilibiliInput.IsBvid(key.Bvid) || key.Cid < 1)
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "A valid BVID and positive CID are required.");
        var now = DateTimeOffset.UtcNow;
        var parameters = new Dictionary<string, string>
        {
            ["bvid"] = key.Bvid,
            ["cid"] = key.Cid.ToString(CultureInfo.InvariantCulture),
            ["qn"] = "80", ["fnval"] = _options.PreferProgressiveMp4 ? "0" : "16",
            ["fnver"] = "0", ["fourk"] = "1", ["platform"] = "pc"
        };
        var result = await GetSignedDataAsync("/x/player/wbi/playurl", parameters, cancellationToken);
        if (_options.PreferProgressiveMp4 && !IsProgressiveMp4(result) && Property(result, "dash").ValueKind != JsonValueKind.Object)
        {
            parameters["fnval"] = "16";
            result = await GetSignedDataAsync("/x/player/wbi/playurl", parameters, cancellationToken);
        }
        if (Property(result, "is_preview").ValueKind == JsonValueKind.True || Number(result, "is_preview") == 1)
            throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "Only a restricted preview is available; sign-in or content entitlement is required.");
        var dash = Property(result, "dash");
        if (dash.ValueKind == JsonValueKind.Object)
        {
            var video = ReadTracks(Property(dash, "video"))
                .Where(t => !t.Codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) &&
                    t.Height <= 1080 && t.Quality <= 80)
                .OrderByDescending(t => t.Codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(t => t.Quality).ThenByDescending(t => t.Bandwidth).FirstOrDefault();
            var audio = ReadTracks(Property(dash, "audio"))
                .Where(t => t.Codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Bandwidth).FirstOrDefault();
            if (video is null || audio is null)
                throw new ContentAccessException(ContentFailureKind.Unsupported, "No complete DASH video and audio pair was available.");
            return Session(key, video, audio, now);
        }
        // A single progressive MP4 contains both audio and video. Multi-segment FLV is outside phase one.
        var progressive = Property(result, "durl");
        if (IsProgressiveMp4(result))
        {
            var url = Text(progressive[0], "url");
            if (TryMediaUri(url, out var uri))
                return Session(key, new(uri!, "mp4", Int(result, "quality")), null, now);
        }
        throw new ContentAccessException(ContentFailureKind.Unsupported, "The returned media format is not supported by the phase-one adapter.");
    }

    public async Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        if (source is null || source.UploaderId < 1 || source.CollectionId < 1 || page < 1 || pageSize is < 1 or > 100)
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "Collection IDs, page and page size must be positive; page size must not exceed 100.");
        string path;
        if (source.Kind == CollectionKind.Season)
            path = $"/x/polymer/web-space/seasons_archives_list?mid={source.UploaderId}&season_id={source.CollectionId}&sort_reverse=false&page_num={page}&page_size={pageSize}";
        else if (source.Kind == CollectionKind.Series)
            path = $"/x/series/archives?mid={source.UploaderId}&series_id={source.CollectionId}&only_normal=true&sort=asc&pn={page}&ps={pageSize}";
        else throw new ContentAccessException(ContentFailureKind.Unsupported, "This collection type is not supported.");
        await EnsureSessionAsync(needWbi: false, forceRefresh: false, cancellationToken);
        var data = Data(await GetJsonAsync(Api + path, cancellationToken));
        var pagination = Property(data, "page");
        var validTotal = Property(pagination, "total").TryGetInt32Safe(out var total);
        var returnedPage = Int(pagination, source.Kind == CollectionKind.Season ? "page_num" : "num");
        var returnedSize = Int(pagination, source.Kind == CollectionKind.Season ? "page_size" : "size");
        var archives = Property(data, "archives");
        if (archives.ValueKind != JsonValueKind.Array || !validTotal || total < 0 ||
            returnedPage != page || returnedSize < 1 || archives.GetArrayLength() > returnedSize)
            throw InvalidResponse("The collection response did not contain a valid page.");
        var entries = new List<CollectionEntry>();
        foreach (var archive in archives.EnumerateArray())
        {
            var bvid = Text(archive, "bvid");
            if (!BilibiliInput.IsBvid(bvid)) throw InvalidResponse("A collection item had no valid BVID.");
            entries.Add(new(bvid, Text(archive, "title"), Seconds(archive, "duration")));
        }
        var meta = Property(data, "meta");
        var title = Text(meta, "name");
        if (string.IsNullOrEmpty(title)) title = $"{source.Kind} {source.CollectionId}";
        return new(source, title, returnedPage, returnedSize, total, entries);
    }

    private static IEnumerable<MediaTrack> ReadTracks(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var element in array.EnumerateArray())
        {
            var url = Text(element, "baseUrl");
            if (string.IsNullOrEmpty(url)) url = Text(element, "base_url");
            if (!TryMediaUri(url, out var uri)) continue;
            var codec = Text(element, "codecs");
            if (!(codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
                  codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
                  codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) ||
                  codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ||
                  codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase))) continue;
            yield return new(uri!, codec, Int(element, "id"), Int(element, "width"), Int(element, "height"), Int(element, "bandwidth"));
        }
    }

    private static bool TryMediaUri(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo);

    private static MediaSession Session(ContentKey key, MediaTrack video, MediaTrack? audio, DateTimeOffset now)
    {
        var expiries = new[] { Expiry(video.Url), audio is null ? null : Expiry(audio.Url) }.OfType<DateTimeOffset>().ToArray();
        return new(key, video, audio, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = $"https://www.bilibili.com/video/{key.Bvid}/"
        }, now, expiries.Length == 0 ? null : expiries.Min());
    }

    private static DateTimeOffset? Expiry(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[0] is not ("deadline" or "expires" or "wsTime")) continue;
            var style = parts[0] == "wsTime" ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (long.TryParse(parts[1], style, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 253402300799)
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return null;
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct, bool acceptAnonymousNav = false)
    {
        var body = await GetTextAsync(url, ct);
        JsonElement root;
        try { using var document = JsonDocument.Parse(body); root = document.RootElement.Clone(); }
        catch (JsonException) { throw InvalidResponse("The service returned an unreadable response."); }
        var code = Number(root, "code");
        if (!Property(root, "code").TryGetInt32Safe(out _)) throw InvalidResponse("The service response did not include a result code.");
        if (code != 0 && !(acceptAnonymousNav && code == -101 &&
                Property(Property(root, "data"), "wbi_img").ValueKind == JsonValueKind.Object))
            throw ServiceFailure((int)code);
        return root;
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri(Referer);
            if (_visitorCookies is { } cookies) request.Headers.TryAddWithoutValidation("Cookie", cookies);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content service could not be reached."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content request timed out."); }
    }

    private static bool IsProgressiveMp4(JsonElement result) =>
        Text(result, "format").Contains("mp4", StringComparison.OrdinalIgnoreCase) &&
        Property(result, "durl") is var urls && urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() == 1;

    private async Task<JsonElement> GetSignedDataAsync(string path, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureSessionAsync(needWbi: true, forceRefresh: attempt > 0, ct);
            var material = _wbi!;
            var query = WbiSigner.Sign(parameters, material.ImageUrl, material.SubUrl, DateTimeOffset.UtcNow);
            try { return Data(await GetJsonAsync(Api + path + "?" + query, ct)); }
            catch (ContentAccessException error) when (attempt == 0 && error.ServiceCode == -403)
            {
                // A rotated signing key can reject an otherwise valid request. Refresh and retry once.
            }
        }
    }

    private async Task EnsureSessionAsync(bool needWbi, bool forceRefresh, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_visitorCookies is null)
            {
                var visitor = Data(await GetJsonAsync(Api + "/x/frontend/finger/spi", ct));
                var buvid3 = Text(visitor, "b_3");
                var buvid4 = Text(visitor, "b_4");
                if (!IsCookieValue(buvid3) || !IsCookieValue(buvid4))
                    throw InvalidResponse("The anonymous visitor credentials were missing or invalid.");
                _visitorCookies = "buvid3=" + buvid3 + "; buvid4=" + buvid4;
            }
            if (needWbi && (forceRefresh || _wbi is null || DateTimeOffset.UtcNow >= _wbi.RefreshAt))
            {
                var nav = await GetJsonAsync(Api + "/x/web-interface/nav", ct, acceptAnonymousNav: true);
                var keys = Property(Data(nav), "wbi_img");
                var img = Text(keys, "img_url");
                var sub = Text(keys, "sub_url");
                // Validate material before caching it; invalid responses cannot poison the cache.
                _ = WbiSigner.Sign(new Dictionary<string, string>(), img, sub, DateTimeOffset.UtcNow);
                _wbi = new(img, sub, DateTimeOffset.UtcNow.AddHours(12));
            }
        }
        finally { _sessionGate.Release(); }
    }

    private static bool IsCookieValue(string value) => value.Length is > 0 and <= 512 &&
        value.All(c => c is > ' ' and < (char)127 && c is not (';' or ','));

    private static JsonElement Data(JsonElement root)
    {
        var data = Property(root, "data");
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw InvalidResponse("The service response did not contain data.");
        return data;
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var result) ? result : default;
    private static string Text(JsonElement value, string name) => Property(value, name) is var property &&
        property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static long Number(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : 0;
    }
    private static int Int(JsonElement value, string name)
    {
        var number = Number(value, name);
        if (number is < int.MinValue or > int.MaxValue) throw InvalidResponse("A service numeric field was out of range.");
        return (int)number;
    }
    private static TimeSpan Seconds(JsonElement value, string name)
    {
        var seconds = Number(value, name);
        if (seconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            throw InvalidResponse("A service duration was out of range.");
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }
    private static ContentAccessException InvalidResponse(string message) => new(ContentFailureKind.InvalidResponse, message);

    private static ContentAccessException HttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new(ContentFailureKind.AuthenticationRequired, "The content service requires sign-in."),
        HttpStatusCode.Forbidden or HttpStatusCode.PreconditionFailed => new(ContentFailureKind.Inaccessible, "The content service blocked this request."),
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new(ContentFailureKind.Removed, "The requested content is unavailable or was removed."),
        HttpStatusCode.TooManyRequests => new(ContentFailureKind.RateLimited, "The content service rate-limited this request; retry later."),
        _ => new(ContentFailureKind.Inaccessible, $"The content service returned HTTP {(int)status}.")
    };

    private static ContentAccessException ServiceFailure(int code) => code switch
    {
        -101 or -102 or 6002105 => new(ContentFailureKind.AuthenticationRequired, "Sign-in or content entitlement is required.", code),
        -403 => new(ContentFailureKind.Inaccessible, "The content service denied access to this content.", code),
        -404 or 62002 or 62004 or 62012 => new(ContentFailureKind.Removed, "The requested content is unavailable or was removed.", code),
        -352 or -509 or -412 => new(ContentFailureKind.RateLimited, "The content service applied a risk or rate limit; retry later.", code),
        -400 => new(ContentFailureKind.InvalidInput, "The content service rejected the content identity or parameters.", code),
        _ => new(ContentFailureKind.Inaccessible, $"The content service rejected this request (code {code}).", code)
    };
}

internal static class JsonElementExtensions
{
    internal static bool TryGetInt32Safe(this JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
