using System.Globalization;
using System.Text.Json;
using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

/// <summary>Account-scoped discovery. Lists only stable video summaries; parts and media are resolved on demand.</summary>
public sealed partial class BilibiliClient : IContentDiscoveryClient
{
    public async Task<FollowingsPage> GetFollowingsAsync(int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ValidateDiscoveryPaging(page, pageSize);
        var session = CaptureDiscoveryAccount(cancellationToken);
        var data = await GetDiscoveryDataAsync("/x/relation/followings", new Dictionary<string, string>
        {
            ["vmid"] = session.Uid.ToString(CultureInfo.InvariantCulture),
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["order"] = "desc", ["order_type"] = "attention"
        }, session, signed: false, cancellationToken);
        var total = RequiredNonnegativeInt(data, "total");
        var list = Property(data, "list");
        ValidateDiscoveryList(list, page, pageSize, total);
        var result = new List<FollowedCreator>();
        var identities = new HashSet<long>();
        foreach (var creator in list.EnumerateArray())
        {
            var uid = Number(creator, "mid");
            var name = Text(creator, "uname");
            if (uid <= 0 || string.IsNullOrWhiteSpace(name) || !identities.Add(uid))
                throw InvalidResponse("The following page contained an invalid or duplicate creator.");
            result.Add(new(uid, name));
        }
        ValidateDiscoveryAccount(session, cancellationToken);
        return new(page, pageSize, total, result);
    }

    public async Task<CreatorVideosPage> GetCreatorVideosAsync(long uploaderId, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ValidateDiscoveryPaging(page, pageSize);
        if (uploaderId <= 0)
            throw new ContentAccessException(ContentFailureKind.InvalidInput, "A positive creator identity is required.");
        var session = CaptureDiscoveryAccount(cancellationToken);
        var data = await GetDiscoveryDataAsync("/x/space/wbi/arc/search", new Dictionary<string, string>
        {
            ["mid"] = uploaderId.ToString(CultureInfo.InvariantCulture),
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = pageSize.ToString(CultureInfo.InvariantCulture), ["order"] = "pubdate"
        }, session, signed: true, cancellationToken);
        var returnedPage = Property(data, "page");
        if (RequiredNonnegativeInt(returnedPage, "pn") != page || RequiredNonnegativeInt(returnedPage, "ps") != pageSize)
            throw InvalidResponse("The creator video response did not match the requested page.");
        var total = RequiredNonnegativeInt(returnedPage, "count");
        var list = Property(Property(data, "list"), "vlist");
        ValidateDiscoveryList(list, page, pageSize, total);
        var result = new List<CreatorVideo>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long? previousPublication = null;
        foreach (var item in list.EnumerateArray())
        {
            var bvid = Text(item, "bvid");
            var title = Text(item, "title");
            var author = Text(item, "author");
            var published = Number(item, "created");
            if (!BilibiliInput.IsBvid(bvid) || !identities.Add(bvid) || Number(item, "mid") != uploaderId ||
                string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author) ||
                published <= 0 || published > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
                previousPublication is { } previous && published > previous)
                throw InvalidResponse("The creator video response contained an invalid identity, date, or publication order.");
            previousPublication = published;
            result.Add(new(bvid, uploaderId, author, title, DateTimeOffset.FromUnixTimeSeconds(published),
                ParseDiscoveryDuration(Text(item, "length"))));
        }
        ValidateDiscoveryAccount(session, cancellationToken);
        return new(uploaderId, page, pageSize, total, result);
    }

    private static void ValidateDiscoveryPaging(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 50)
            throw new ContentAccessException(ContentFailureKind.InvalidInput,
                "Page and page size must be positive; discovery page size must not exceed 50.");
    }

    private static int RequiredNonnegativeInt(JsonElement value, string name)
    {
        if (!Property(value, name).TryGetInt32Safe(out var result) || result < 0)
            throw InvalidResponse("The discovery response contained missing or invalid pagination.");
        return result;
    }

    private static void ValidateDiscoveryList(JsonElement list, int page, int pageSize, int total)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > pageSize || list.GetArrayLength() > total ||
            list.GetArrayLength() == 0 && (long)(page - 1) * pageSize < total)
            throw InvalidResponse("The discovery response did not contain a complete valid page.");
    }

    private static TimeSpan ParseDiscoveryDuration(string text)
    {
        var fields = text.Split(':');
        if (fields.Length is not (2 or 3)) throw InvalidResponse("A video duration was missing or invalid.");
        long seconds = 0;
        for (var index = 0; index < fields.Length; index++)
        {
            if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < 0 || index > 0 && value >= 60)
                throw InvalidResponse("A video duration was invalid.");
            seconds = checked(seconds * 60 + value);
        }
        if (seconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            throw InvalidResponse("A video duration was out of range.");
        return TimeSpan.FromSeconds(seconds);
    }

    // This private object deliberately has no generated record diagnostics that could include cookies.
    private sealed class DiscoveryAccount(long uid, int version, string cookies)
    {
        public long Uid { get; } = uid;
        public int Version { get; } = version;
        public string Cookies { get; } = cookies;
    }

    private DiscoveryAccount CaptureDiscoveryAccount(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_accountGate)
        {
            if (_account is null || _accountCookies is null)
                throw new ContentAccessException(ContentFailureKind.AuthenticationRequired,
                    "Sign in to discover followed creators and subscription updates.");
            return new(_account.Uid, _sessionVersion, _accountCookies);
        }
    }

    private void ValidateDiscoveryAccount(DiscoveryAccount session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_accountGate)
            if (_sessionVersion != session.Version || _account?.Uid != session.Uid)
                throw new OperationCanceledException("The discovery account session was superseded.", ct);
    }

    private async Task<JsonElement> GetDiscoveryDataAsync(string path, Dictionary<string, string> parameters,
        DiscoveryAccount session, bool signed, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ValidateDiscoveryAccount(session, ct);
                string query;
                if (signed)
                {
                    var material = await GetDiscoveryWbiAsync(session, forceRefresh: attempt > 0, ct);
                    query = WbiSigner.Sign(parameters, material.ImageUrl, material.SubUrl, DateTimeOffset.UtcNow);
                }
                else query = string.Join('&', parameters.Select(pair =>
                    Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
                ValidateDiscoveryAccount(session, ct);
                var data = Data(await GetJsonAsync(Api + path + "?" + query, ct, cookieOverride: session.Cookies));
                ValidateDiscoveryAccount(session, ct);
                return data;
            }
            catch (ContentAccessException error) when (signed && attempt == 0 && error.ServiceCode == -403)
            {
                ValidateDiscoveryAccount(session, ct);
                // Match metadata resolution: one refresh for a rotated WBI key, with no anonymous fallback.
            }
            catch
            {
                ValidateDiscoveryAccount(session, ct);
                throw;
            }
        }
    }

    private async Task<WbiMaterial> GetDiscoveryWbiAsync(DiscoveryAccount session, bool forceRefresh, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            lock (_accountGate)
            {
                ValidateDiscoveryAccount(session, ct);
                if (!forceRefresh && _wbi is { } existing && DateTimeOffset.UtcNow < existing.RefreshAt) return existing;
            }
            // Validated account credentials already supply a web session. Do not require anonymous visitor
            // provisioning here: its independent risk gate must not block account discovery.
            var nav = Data(await GetJsonAsync(Api + "/x/web-interface/nav", ct, cookieOverride: session.Cookies));
            if (Number(nav, "mid") != session.Uid ||
                Property(nav, "isLogin").ValueKind != JsonValueKind.True && Number(nav, "isLogin") != 1)
                throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "The discovery account session has expired.");
            var keys = Property(nav, "wbi_img");
            var material = new WbiMaterial(Text(keys, "img_url"), Text(keys, "sub_url"), DateTimeOffset.UtcNow.AddHours(12));
            _ = WbiSigner.Sign(new Dictionary<string, string>(), material.ImageUrl, material.SubUrl, DateTimeOffset.UtcNow);
            lock (_accountGate)
            {
                ValidateDiscoveryAccount(session, ct);
                _wbi = material;
            }
            return material;
        }
        finally { _sessionGate.Release(); }
    }
}
