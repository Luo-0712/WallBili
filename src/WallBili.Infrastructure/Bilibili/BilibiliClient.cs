using System.Globalization;
using System.Net;
using System.Text.Json;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Web WBI adapter with unified account/visitor session management. Credentials stay in this
/// instance and are only sent to the Bilibili API endpoints that require them; CDN playback keeps using
/// media addresses plus the published Referer and User-Agent.</summary>
public sealed partial class BilibiliClient(HttpClient httpClient) : IContentClient
{
    private const string Api = "https://api.bilibili.com";
    private const string Passport = "https://passport.bilibili.com";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private const string Referer = "https://www.bilibili.com/";
    /// <summary>DASH plus every high-tier and special-audio flag (HDR/4K/8K, Dolby, Hi-Res lossless, AV1).</summary>
    private const string DashFnval = "4048";
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _accountGate = new();
    private string? _visitorCookies;
    private WbiMaterial? _wbi;
    private string? _accountCookies;
    private BilibiliAccount? _account;
    private int _sessionVersion;
    private long _loginAttemptVersion;
    private string? _activeQrcodeKey;
    private sealed record WbiMaterial(string ImageUrl, string SubUrl, DateTimeOffset RefreshAt);

    /// <summary>Notified after the account session is replaced, cleared, or expired.</summary>
    public event EventHandler<BilibiliAccountChangedEventArgs>? AccountChanged;

    public BilibiliAccount? CurrentAccount() { lock (_accountGate) return _account; }

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
            parts.OrderBy(p => p.Page).ToArray(), ReadRelatedCollections(video));
    }

    public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default) =>
        ResolveMediaAsync(key, null, cancellationToken);

    public async Task<MediaSession> ResolveMediaAsync(ContentKey key, MediaSelectionPreferences? selection,
        CancellationToken cancellationToken = default)
    {
        if (key is null || !BilibiliInput.IsBvid(key.Bvid) || key.Cid < 1)
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "A valid BVID and positive CID are required.");
        selection ??= MediaSelectionPreferences.Automatic;
        try
        {
            return await ResolvePlayUrlAsync(key, selection, cancellationToken);
        }
        catch (ContentAccessException ex)
        {
            if (ex.Kind != ContentFailureKind.AuthenticationRequired || CurrentAccount() is null)
                throw;
            // Only a confirmed expired account is cleared and retried once as a guest. Network timeouts,
            // risk control, removed content and entitlement-gated previews never clear the session.
            if (!await IsAccountSessionExpiredAsync(cancellationToken)) throw;
            ClearAccount();
            return await ResolvePlayUrlAsync(key, selection, cancellationToken);
        }
    }

    private async Task<MediaSession> ResolvePlayUrlAsync(ContentKey key, MediaSelectionPreferences selection,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var parameters = new Dictionary<string, string>
        {
            ["bvid"] = key.Bvid,
            ["cid"] = key.Cid.ToString(CultureInfo.InvariantCulture),
            ["fnval"] = DashFnval, ["fnver"] = "0", ["fourk"] = "1", ["platform"] = "pc"
        };
        var result = await GetSignedDataAsync("/x/player/wbi/playurl", parameters, ct);
        if (Property(result, "is_preview").ValueKind == JsonValueKind.True || Number(result, "is_preview") == 1)
            throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "Only a restricted preview is available; sign-in or content entitlement is required.");
        var dash = Property(result, "dash");
        if (dash.ValueKind == JsonValueKind.Object)
            return ResolveDash(key, result, dash, selection, now);
        // A single progressive MP4 contains both audio and video. Multi-segment FLV is outside phase one.
        if (IsProgressiveMp4(result))
        {
            var url = Text(Property(result, "durl")[0], "url");
            if (TryMediaUri(url, out var uri))
            {
                var labels = ReadQualityLabels(result);
                var quality = Int(result, "quality");
                var label = labels.GetValueOrDefault(quality) ?? $"画质 {quality.ToString(CultureInfo.InvariantCulture)}";
                return Session(key, new(uri!, "mp4", quality), null, now, new MediaSelectionSummary(
                    [new MediaQualityOption(quality.ToString(CultureInfo.InvariantCulture), label)],
                    [], label, "内嵌音轨，不可单独选择", EmbeddedAudio: true));
            }
        }
        throw new ContentAccessException(ContentFailureKind.Unsupported, "The returned media format is not supported by this adapter.");
    }

    /// <summary>Builds the quality options from the streams actually returned by upstream. Declared tiers
    /// without a stream are never listed; multiple codecs of one tier merge into a single option.</summary>
    private static MediaSession ResolveDash(ContentKey key, JsonElement result, JsonElement dash,
        MediaSelectionPreferences selection, DateTimeOffset now)
    {
        var videos = ReadTracks(Property(dash, "video")).ToList();
        if (videos.Count == 0)
            throw new ContentAccessException(ContentFailureKind.Unsupported, "No DASH video stream was available.");
        var normalAudio = ReadTracks(Property(dash, "audio")).ToList();
        var dolbyAudio = ReadTracks(Property(Property(dash, "dolby"), "audio")).ToList();
        // Unlike normal/Dolby audio, flac.audio is one track object, not an array.
        var flacAudio = new[] { ReadTrack(Property(Property(dash, "flac"), "audio")) }.OfType<MediaTrack>().ToList();
        var labels = ReadQualityLabels(result);

        // Video options are ordered by tier rank (the upstream quality code is monotonic with quality).
        var videoGroups = videos.GroupBy(t => t.Quality).OrderByDescending(g => g.Key).ToList();
        var videoOptions = videoGroups.Select(g => new MediaQualityOption(
            g.Key.ToString(CultureInfo.InvariantCulture),
            labels.GetValueOrDefault(g.Key) ?? $"{g.Max(t => t.Height)}p（档位 {g.Key.ToString(CultureInfo.InvariantCulture)}）")).ToArray();
        var (videoTier, videoFallback) = SelectVideoTier(videoOptions, selection.VideoQualityId);
        var video = videoGroups.First(g => g.Key.ToString(CultureInfo.InvariantCulture) == videoTier.Id)
            .OrderBy(PickVideoCodecRank).ThenByDescending(t => t.Bandwidth).First();
        var selectedVideo = $"{videoTier.Label}（{CodecShortName(video.Codec)}）";

        var audioOptions = new List<MediaQualityOption>();
        var normalGroups = normalAudio.GroupBy(t => t.Quality).OrderByDescending(g => g.Max(t => t.Bandwidth)).ToList();
        foreach (var group in normalGroups)
        {
            var track = group.OrderByDescending(t => t.Bandwidth).First();
            audioOptions.Add(new(group.Key.ToString(CultureInfo.InvariantCulture),
                $"{DescribeBandwidth(track.Bandwidth)}（{CodecShortName(track.Codec)}）"));
        }
        // Special tiers appear only when upstream actually returned their streams.
        if (dolbyAudio.Count > 0)
            audioOptions.Add(new(DolbyQuality.ToString(CultureInfo.InvariantCulture), "杜比全景声", Special: true));
        if (flacAudio.Count > 0)
            audioOptions.Add(new(FlacQuality.ToString(CultureInfo.InvariantCulture), "Hi-Res 无损", Special: true));

        MediaTrack? audio = null;
        string? selectedAudio = null;
        string? audioFallback = null;
        if (normalAudio.Count == 0 && dolbyAudio.Count == 0 && flacAudio.Count == 0)
            selectedAudio = "无音轨";
        else
        {
            MediaTrack chosen;
            if (selection.AudioQualityId is not { } preferred)
                chosen = normalAudio.Count > 0
                    ? normalAudio.OrderByDescending(t => t.Bandwidth).First()
                    : SpecialAudio(dolbyAudio, flacAudio);
            else if (preferred == DolbyQuality.ToString(CultureInfo.InvariantCulture) && dolbyAudio.Count > 0)
                chosen = dolbyAudio.OrderByDescending(t => t.Bandwidth).First();
            else if (preferred == FlacQuality.ToString(CultureInfo.InvariantCulture) && flacAudio.Count > 0)
                chosen = flacAudio.OrderByDescending(t => t.Bandwidth).First();
            else
            {
                var preferredIsSpecial = int.TryParse(preferred, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var preferredId) && preferredId is DolbyQuality or FlacQuality;
                if (preferredIsSpecial)
                {
                    if (normalGroups.Count > 0)
                    {
                        // A missing special tier falls back to the highest normal tier, not an id-based one.
                        chosen = normalGroups[0].OrderByDescending(t => t.Bandwidth).First();
                        audioFallback = "特殊音轨不可用，已回退至最高普通音质";
                    }
                    else chosen = SpecialAudio(dolbyAudio, flacAudio);
                }
                else chosen = SelectNormalAudio(normalGroups, preferred);
            }
            audio = chosen;
            selectedAudio = chosen.Quality is DolbyQuality or FlacQuality
                ? audioOptions.First(o => o.Id == chosen.Quality.ToString(CultureInfo.InvariantCulture)).Label
                : $"{DescribeBandwidth(chosen.Bandwidth)}（{CodecShortName(chosen.Codec)}）";
        }
        return Session(key, video, audio, now, new MediaSelectionSummary(videoOptions, audioOptions,
            selectedVideo, selectedAudio, videoFallback, audioFallback, NoAudio: audio is null));
    }

    private const int DolbyQuality = 30250;
    private const int FlacQuality = 30251;

    private static MediaTrack SpecialAudio(List<MediaTrack> dolby, List<MediaTrack> flac) =>
        dolby.Count > 0 ? dolby.OrderByDescending(t => t.Bandwidth).First()
            : flac.OrderByDescending(t => t.Bandwidth).First();

    private static MediaTrack SelectNormalAudio(List<IGrouping<int, MediaTrack>> normalGroups, string preferredId)
    {
        // Normal audio tiers are monotonic: a missing tier falls back to the highest tier not above the target.
        var exact = normalGroups.FirstOrDefault(g => g.Key.ToString(CultureInfo.InvariantCulture) == preferredId);
        if (exact is not null) return exact.OrderByDescending(t => t.Bandwidth).First();
        if (int.TryParse(preferredId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
        {
            var lower = normalGroups.FirstOrDefault(g => g.Key <= target);
            if (lower is not null) return lower.OrderByDescending(t => t.Bandwidth).First();
        }
        return normalGroups[^1].OrderByDescending(t => t.Bandwidth).First();
    }

    private static (MediaQualityOption Tier, string? Fallback) SelectVideoTier(MediaQualityOption[] ranked,
        string? preferredId)
    {
        // ranked is ordered from the highest to the lowest available tier.
        if (preferredId is null) return (ranked[0], null);
        var exact = ranked.FirstOrDefault(o => o.Id == preferredId);
        if (exact is not null) return (exact, null);
        if (int.TryParse(preferredId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
        {
            var lower = ranked.FirstOrDefault(o => int.Parse(o.Id, CultureInfo.InvariantCulture) <= target);
            if (lower is not null) return (lower, $"目标画质不可用，已回退至{lower.Label}");
        }
        return (ranked[^1], $"目标画质不可用，已回退至{ranked[^1].Label}");
    }

    private static int PickVideoCodecRank(MediaTrack track) => track.Codec.ToLowerInvariant() switch
    {
        var c when c.StartsWith("avc") => 0,
        var c when c.StartsWith("hev") || c.StartsWith("hvc") => 1,
        var c when c.StartsWith("av01") => 2,
        _ => 3
    };

    private static string CodecShortName(string codec) => codec.ToLowerInvariant() switch
    {
        var c when c.StartsWith("avc") => "AVC",
        var c when c.StartsWith("hev") || c.StartsWith("hvc") => "HEVC",
        var c when c.StartsWith("av01") => "AV1",
        var c when c.StartsWith("mp4a") => "AAC",
        var c when c.StartsWith("ec-3") => "杜比",
        var c when c.StartsWith("flac") => "FLAC",
        _ => codec
    };

    private static string DescribeBandwidth(int bandwidth) => bandwidth > 0
        ? $"{Math.Max(1, (int)Math.Round(bandwidth / 1000.0))} kbps" : "未知码率";

    /// <summary>Maps declared quality descriptions. support_formats wins; accept_quality/accept_description
    /// is the fallback. Unknown ids are labelled from the stream parameters instead of being dropped.</summary>
    private static Dictionary<int, string> ReadQualityLabels(JsonElement result)
    {
        var labels = new Dictionary<int, string>();
        var formats = Property(result, "support_formats");
        if (formats.ValueKind == JsonValueKind.Array)
            foreach (var format in formats.EnumerateArray())
            {
                var quality = Int(format, "quality");
                if (quality <= 0) continue;
                var description = string.IsNullOrWhiteSpace(Text(format, "new_desc"))
                    ? Text(format, "display_desc") : Text(format, "new_desc");
                if (description.Length > 0) labels[quality] = description;
            }
        var qualities = Property(result, "accept_quality");
        var descriptions = Property(result, "accept_description");
        if (qualities.ValueKind == JsonValueKind.Array && descriptions.ValueKind == JsonValueKind.Array)
        {
            var qualityList = qualities.EnumerateArray().ToList();
            var descriptionList = descriptions.EnumerateArray().ToList();
            for (var i = 0; i < Math.Min(qualityList.Count, descriptionList.Count); i++)
            {
                if (!qualityList[i].TryGetInt32Safe(out var quality) || quality <= 0 ||
                    labels.ContainsKey(quality) || descriptionList[i].ValueKind != JsonValueKind.String) continue;
                var description = descriptionList[i].GetString() ?? "";
                if (description.Length > 0) labels[quality] = description;
            }
        }
        return labels;
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

    /// <summary>Requests a fresh web QR challenge. Any previous challenge becomes superseded.</summary>
    public async Task<QrLoginChallenge> CreateQrLoginAsync(CancellationToken cancellationToken = default)
    {
        // Invalidate the previous attempt before requesting a replacement, not after the network returns.
        long attempt;
        lock (_accountGate) { attempt = ++_loginAttemptVersion; _activeQrcodeKey = null; }
        var root = await GetJsonAsync(Passport + "/x/passport-login/web/qrcode/generate", cancellationToken);
        var data = Data(root);
        var url = Text(data, "url");
        var key = Text(data, "qrcode_key");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var login) || login.Scheme != "https" ||
            key.Length is < 16 or > 128)
            throw InvalidResponse("The QR login challenge was missing or invalid.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_accountGate)
            if (attempt == _loginAttemptVersion) _activeQrcodeKey = key;
        return new(login, key);
    }

    /// <summary>Invalidates pending QR generation, polling and validation without signing out the account.</summary>
    public void CancelQrLogin()
    {
        lock (_accountGate) { ++_loginAttemptVersion; _activeQrcodeKey = null; }
    }

    /// <summary>Polls a web QR challenge. A successful poll validates the candidate credentials through the
    /// navigation endpoint before the session is replaced; late polls of superseded challenges are inert.</summary>
    public async Task<QrLoginPoll> PollQrLoginAsync(string qrcodeKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(qrcodeKey))
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "A QR challenge key is required.");
        long attempt;
        lock (_accountGate)
        {
            if (_activeQrcodeKey != qrcodeKey)
                return new(QrLoginState.Superseded);
            attempt = _loginAttemptVersion;
        }
        var (root, setCookies) = await GetJsonWithCookiesAsync(
            Passport + "/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(qrcodeKey),
            cancellationToken);
        if (Number(root, "code") != 0) throw ServiceFailure((int)Number(root, "code"));
        var data = Data(root);
        switch ((int)Number(data, "code"))
        {
            case 86101:
                return new(QrLoginState.WaitingScan);
            case 86090:
                return new(QrLoginState.WaitingConfirm);
            case 86038:
                return new(QrLoginState.Expired);
            case 0:
                lock (_accountGate)
                    if (_activeQrcodeKey != qrcodeKey || attempt != _loginAttemptVersion)
                        return new(QrLoginState.Superseded);
                var cookieText = BilibiliCookieText.FromSetCookieValues(setCookies);
                if (BilibiliCookieText.FindValue(cookieText, "SESSDATA") is null)
                    throw InvalidResponse("The login response did not contain session credentials.");
                var account = await GetAccountFromNavAsync(cookieText, cancellationToken);
                if (!TryApplyAccount(account, cookieText, attempt, cancellationToken))
                    return new(QrLoginState.Superseded);
                return new(QrLoginState.Succeeded, cookieText, account);
            default:
                throw ServiceFailure((int)Number(data, "code"));
        }
    }

    /// <summary>Validates candidate cookie text through the navigation endpoint and only then replaces the
    /// current session. A failed validation or a temporary network error keeps the previous account.</summary>
    public async Task<BilibiliAccount> ImportCookiesAsync(string cookieText, CancellationToken cancellationToken = default)
    {
        var normalized = BilibiliCookieText.Normalize(cookieText);
        long attempt;
        lock (_accountGate) { attempt = ++_loginAttemptVersion; _activeQrcodeKey = null; }
        var account = await GetAccountFromNavAsync(normalized, cancellationToken);
        if (!TryApplyAccount(account, normalized, attempt, cancellationToken))
            throw new OperationCanceledException("The login attempt was superseded.", cancellationToken);
        return account;
    }

    /// <summary>Leaves the WallBili account only; the all-site session stays valid on Bilibili.</summary>
    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearAccount();
        return Task.CompletedTask;
    }

    private async Task<BilibiliAccount> GetAccountFromNavAsync(string cookieText, CancellationToken ct)
    {
        var data = Data(await GetJsonAsync(Api + "/x/web-interface/nav", ct, cookieOverride: cookieText));
        if (Number(data, "mid") <= 0 || Text(data, "uname").Length == 0 ||
            (Property(data, "isLogin").ValueKind != JsonValueKind.True && Number(data, "isLogin") != 1))
            throw new ContentAccessException(ContentFailureKind.AuthenticationRequired,
                "The credentials were rejected or have expired.");
        var vip = Number(data, "vipStatus") == 1 ||
            (Property(data, "vip").ValueKind == JsonValueKind.Object && Number(Property(data, "vip"), "status") == 1);
        return new(Number(data, "mid"), Text(data, "uname"), vip);
    }

    /// <summary>Confirms whether the account cookie is really expired. Network errors, risk control and
    /// unreadable responses never count as an expired session.</summary>
    private async Task<bool> IsAccountSessionExpiredAsync(CancellationToken ct)
    {
        string cookies;
        lock (_accountGate) cookies = _accountCookies!;
        try
        {
            var data = Data(await GetJsonAsync(Api + "/x/web-interface/nav", ct, cookieOverride: cookies));
            return Property(data, "isLogin").ValueKind != JsonValueKind.True && Number(data, "isLogin") != 1;
        }
        catch (ContentAccessException ex)
        {
            return ex.Kind == ContentFailureKind.AuthenticationRequired;
        }
    }

    private bool TryApplyAccount(BilibiliAccount account, string cookieText, long attempt, CancellationToken ct)
    {
        BilibiliAccountChangedEventArgs args;
        lock (_accountGate)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt != _loginAttemptVersion) return false;
            _accountCookies = cookieText;
            _account = account;
            _activeQrcodeKey = null;
            ++_loginAttemptVersion;
            _wbi = null;
            args = new(account, ++_sessionVersion);
        }
        AccountChanged?.Invoke(this, args);
        return true;
    }

    private void ClearAccount()
    {
        BilibiliAccountChangedEventArgs args;
        lock (_accountGate)
        {
            // Signing out must invalidate pending login work even while already anonymous.
            ++_loginAttemptVersion;
            _activeQrcodeKey = null;
            if (_accountCookies is null && _account is null) return;
            _accountCookies = null;
            _account = null;
            _wbi = null;
            args = new(null, ++_sessionVersion);
        }
        AccountChanged?.Invoke(this, args);
    }

    private static IEnumerable<MediaTrack> ReadTracks(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var element in array.EnumerateArray())
            if (ReadTrack(element) is { } track) yield return track;
    }

    private static MediaTrack? ReadTrack(JsonElement element)
    {
        var url = Text(element, "baseUrl");
        if (string.IsNullOrEmpty(url)) url = Text(element, "base_url");
        if (!TryMediaUri(url, out var uri)) return null;
        var codec = Text(element, "codecs");
        if (!(codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) ||
              codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase))) return null;
        return new(uri!, codec, Int(element, "id"), Int(element, "width"), Int(element, "height"), Int(element, "bandwidth"));
    }

    private static bool TryMediaUri(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo);

    private static MediaSession Session(ContentKey key, MediaTrack video, MediaTrack? audio, DateTimeOffset now,
        MediaSelectionSummary? selection = null)
    {
        var expiries = new[] { Expiry(video.Url), audio is null ? null : Expiry(audio.Url) }.OfType<DateTimeOffset>().ToArray();
        return new(key, video, audio, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = $"https://www.bilibili.com/video/{key.Bvid}/"
        }, now, expiries.Length == 0 ? null : expiries.Min(), selection);
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

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct, bool acceptAnonymousNav = false,
        string? cookieOverride = null)
    {
        var body = await GetTextAsync(url, ct, cookieOverride);
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

    private async Task<(JsonElement Root, IReadOnlyList<string> SetCookies)> GetJsonWithCookiesAsync(string url,
        CancellationToken ct)
    {
        string body;
        List<string> setCookies;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri(Referer);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
            setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.ToList() : [];
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content service could not be reached."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content request timed out."); }
        JsonElement root;
        try { using var document = JsonDocument.Parse(body); root = document.RootElement.Clone(); }
        catch (JsonException) { throw InvalidResponse("The service returned an unreadable response."); }
        if (!Property(root, "code").TryGetInt32Safe(out var code) || code != 0)
        {
            if (code != 0) throw ServiceFailure((int)code);
            throw InvalidResponse("The service response did not include a result code.");
        }
        return (root, setCookies);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct, string? cookieOverride = null)
    {
        try
        {
            string? cookie = cookieOverride ?? RequestCookieHeader();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri(Referer);
            if (cookie is { Length: > 0 }) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content service could not be reached."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ContentAccessException(ContentFailureKind.Network, "The content request timed out."); }
    }

    private string? RequestCookieHeader()
    {
        lock (_accountGate)
            if (_accountCookies is { } account) return account;
        return _visitorCookies;
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
