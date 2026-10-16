using System.Net;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliLoginTests
{
    private const string Key = "BV1GJ411x7h7";
    private const string AnonymousNav = """
        {"code":-101,"data":{"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    private const string LoggedInNav = """
        {"code":0,"data":{"isLogin":true,"mid":42,"uname":"Uploader","vipStatus":1,"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    private const string ExpiredNav = """{"code":-101,"message":"not logged in","data":{}}""";
    private const string Media = """
        {"code":0,"data":{"dash":{
          "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
            "width":1920,"height":1080,"bandwidth":3000000}],
          "audio":[{"id":30280,"baseUrl":"https://media.example/audio","codecs":"mp4a.40.2","bandwidth":192000}]}}}
        """;

    private static bool HasCookie(HttpRequestMessage request, string fragment) =>
        request.Headers.TryGetValues("Cookie", out var cookies) &&
        cookies.Any(value => value.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task ImportsValidCookiesAfterNavValidationAndNotifies()
    {
        string? candidateCookie = null;
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav")
            {
                candidateCookie = request.Headers.GetValues("Cookie").Single();
                return StubHttpHandler.Json(HasCookie(request, "SESSDATA=valid-token") ? LoggedInNav : AnonymousNav);
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}");
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        BilibiliAccountChangedEventArgs? notification = null;
        client.AccountChanged += (_, args) => notification = args;

        var account = await client.ImportCookiesAsync(
            "SESSDATA=valid-token; bili_jct=csrf; DedeUserID=42; DedeUserID__ckMd5=hash; buvid3=device; unrelated=drop",
            TestContext.Current.CancellationToken);

        Assert.Equal(new BilibiliAccount(42, "Uploader", true), account);
        Assert.Equal(account, client.CurrentAccount());
        Assert.NotNull(notification);
        Assert.Equal(account, notification!.Account);
        Assert.Equal(1, notification.SessionVersion);
        // Only the fields needed for signed-in and guest requests are retained; encoding stays untouched.
        Assert.Equal("SESSDATA=valid-token; bili_jct=csrf; DedeUserID=42; DedeUserID__ckMd5=hash; buvid3=device",
            candidateCookie);
    }

    [Theory]
    [InlineData("bili_jct=csrf")] // No SESSDATA at all.
    [InlineData("SESSDATA=line\r\nbreak")] // Line breaks are rejected.
    [InlineData("SESSDATA=line\nbreak")]
    [InlineData("not-a-pair")] // Missing the '=' separator.
    [InlineData("SESSDATA=")] // Empty value.
    public async Task RejectsMalformedCookieTextBeforeAnyNetworkCall(string text)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No network request expected."));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            client.ImportCookiesAsync(text, TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.InvalidInput, failure.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExpiredImportKeepsThePreviousAccount()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(HasCookie(request, "SESSDATA=valid-token") ? LoggedInNav : ExpiredNav)
            : throw new InvalidOperationException("Unexpected endpoint."));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var account = await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            client.ImportCookiesAsync("SESSDATA=expired-token", TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.AuthenticationRequired, failure.Kind);
        Assert.Equal(account, client.CurrentAccount()); // The candidate never replaced the live session.
    }

    [Fact]
    public async Task NetworkErrorDuringImportKeepsThePreviousAccount()
    {
        var navCalls = 0;
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/x/web-interface/nav")
                throw new InvalidOperationException("Unexpected endpoint.");
            navCalls++;
            return navCalls == 1 ? StubHttpHandler.Json(LoggedInNav) : throw new HttpRequestException("offline");
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var account = await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            client.ImportCookiesAsync("SESSDATA=other-token", TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.Network, failure.Kind);
        Assert.Equal(account, client.CurrentAccount());
    }

    [Fact]
    public async Task QrLoginWalksThroughScanStatesAndAppliesValidatedCredentials()
    {
        var pollCalls = 0;
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/passport-login/web/qrcode/generate" => StubHttpHandler.Json("""
                {"code":0,"data":{"url":"https://passport.bilibili.com/h5-app/passport/login/scan?qrcode_key=challenge-key-0123456789","qrcode_key":"challenge-key-0123456789"}}
                """),
            "/x/passport-login/web/qrcode/poll" => (++pollCalls) switch
            {
                1 => StubHttpHandler.Json("""{"code":0,"data":{"code":86101,"message":"未扫码"}}"""),
                2 => StubHttpHandler.Json("""{"code":0,"data":{"code":86090,"message":"已扫码未确认"}}"""),
                _ => QrSuccess()
            },
            "/x/web-interface/nav" => StubHttpHandler.Json(HasCookie(request, "SESSDATA=qr-token") ? LoggedInNav : AnonymousNav),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        var challenge = await client.CreateQrLoginAsync(TestContext.Current.CancellationToken);
        Assert.Equal("challenge-key-0123456789", challenge.QrcodeKey);
        Assert.StartsWith("https://passport.bilibili.com/", challenge.LoginUrl.AbsoluteUri);

        Assert.Equal(QrLoginState.WaitingScan,
            (await client.PollQrLoginAsync(challenge.QrcodeKey, TestContext.Current.CancellationToken)).State);
        Assert.Equal(QrLoginState.WaitingConfirm,
            (await client.PollQrLoginAsync(challenge.QrcodeKey, TestContext.Current.CancellationToken)).State);
        var success = await client.PollQrLoginAsync(challenge.QrcodeKey, TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginState.Succeeded, success.State);
        Assert.Equal(new BilibiliAccount(42, "Uploader", true), success.Account);
        Assert.Equal("SESSDATA=qr-token; bili_jct=qr-csrf; DedeUserID=42; DedeUserID__ckMd5=ignored; buvid3=qr-device",
            success.CookieText);
        Assert.Equal(success.Account, client.CurrentAccount());
    }

    private static HttpResponseMessage Generate(int call)
    {
        var key = $"challenge-key-{call:D16}";
        return StubHttpHandler.Json("{\"code\":0,\"data\":{\"url\":\"https://passport.bilibili.com/h5-app/passport/login/scan?qrcode_key="
            + key + "\",\"qrcode_key\":\"" + key + "\"}}");
    }

    private static HttpResponseMessage QrSuccess()
    {
        var response = StubHttpHandler.Json("""{"code":0,"data":{"code":0,"message":"0","url":"https://passport.bilibili.com/0-0-x.jpg","timestamp":1}}""");
        response.Headers.Add("Set-Cookie", "SESSDATA=qr-token; Path=/; Domain=.bilibili.com; HttpOnly");
        response.Headers.Add("Set-Cookie", "bili_jct=qr-csrf; Path=/; Domain=.bilibili.com");
        response.Headers.Add("Set-Cookie", "DedeUserID=42; Path=/; Domain=.bilibili.com");
        response.Headers.Add("Set-Cookie", "DedeUserID__ckMd5=ignored; Path=/; Domain=.bilibili.com");
        response.Headers.Add("Set-Cookie", "buvid3=qr-device; Path=/; Domain=.bilibili.com");
        response.Headers.Add("Set-Cookie", "Expires=gone; Path=/");
        return response;
    }

    [Fact]
    public async Task ExpiredQrChallengeIsReported()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/passport-login/web/qrcode/generate" => StubHttpHandler.Json("""
                {"code":0,"data":{"url":"https://passport.bilibili.com/h5-app/passport/login/scan?qrcode_key=challenge-key-0123456789","qrcode_key":"challenge-key-0123456789"}}
                """),
            "/x/passport-login/web/qrcode/poll" => StubHttpHandler.Json("""{"code":0,"data":{"code":86038,"message":"二维码已失效"}}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        var challenge = await client.CreateQrLoginAsync(TestContext.Current.CancellationToken);
        Assert.Equal(QrLoginState.Expired,
            (await client.PollQrLoginAsync(challenge.QrcodeKey, TestContext.Current.CancellationToken)).State);
        Assert.Null(client.CurrentAccount());
    }

    [Fact]
    public async Task ReissuedChallengeMakesLatePollsInert()
    {
        var keyCalls = 0;
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/passport-login/web/qrcode/generate" => Generate(++keyCalls),
            "/x/passport-login/web/qrcode/poll" => QrSuccess(),
            "/x/web-interface/nav" => StubHttpHandler.Json(HasCookie(request, "SESSDATA=qr-token") ? LoggedInNav : AnonymousNav),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        var first = await client.CreateQrLoginAsync(TestContext.Current.CancellationToken);
        var second = await client.CreateQrLoginAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(first.QrcodeKey, second.QrcodeKey);

        var late = await client.PollQrLoginAsync(first.QrcodeKey, TestContext.Current.CancellationToken);
        Assert.Equal(QrLoginState.Superseded, late.State); // Late success of the old key cannot apply.
        Assert.Null(client.CurrentAccount());
        var current = await client.PollQrLoginAsync(second.QrcodeKey, TestContext.Current.CancellationToken);
        Assert.Equal(QrLoginState.Succeeded, current.State);
        Assert.NotNull(client.CurrentAccount());
    }

    [Fact]
    public async Task ConfirmedExpiredSessionIsClearedAndRetriedOnceAsGuest()
    {
        var navCalls = 0;
        var playCalls = 0;
        var playCookies = new List<string>();
        using var handler = new StubHttpHandler(request =>
        {
            // Call 1 validates the import; call 2 refreshes the WBI material while the account is still live;
            // call 3 is the expiry recheck after playurl rejected the session; call 4 re-fetches the WBI
            // material for the guest retry because clearing the account invalidated the cache.
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav")
                return StubHttpHandler.Json((++navCalls) == 3 ? ExpiredNav : LoggedInNav);
            if (request.RequestUri.AbsolutePath == "/x/player/wbi/playurl")
            {
                playCookies.Add(request.Headers.TryGetValues("Cookie", out var c)
                    ? string.Join("; ", c) : "");
                return (++playCalls) switch
                {
                    1 => StubHttpHandler.Json("""{"code":-101,"message":"账号未登录"}"""),
                    _ => StubHttpHandler.Json(Media)
                };
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}");
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);
        BilibiliAccountChangedEventArgs? notification = null;
        client.AccountChanged += (_, args) => notification = args;

        // The playurl rejection triggers a nav recheck; the anonymous nav proves the session is gone,
        // so the account is cleared and the resolve is retried once with visitor credentials.
        var session = await client.ResolveMediaAsync(new(Key, 101), TestContext.Current.CancellationToken);

        Assert.Equal("https://media.example/video", session.Video.Url.AbsoluteUri);
        Assert.Equal(2, playCalls);
        Assert.Null(client.CurrentAccount());
        Assert.NotNull(notification);
        Assert.Null(notification!.Account);
        // The retry must not leak the dropped account cookie; it carries only visitor credentials.
        Assert.Contains("valid-token", playCookies[0]);
        Assert.DoesNotContain("valid-token", playCookies[1]);
        Assert.Contains("buvid3=visitor-three", playCookies[1]);
    }

    [Fact]
    public async Task EntitlementRejectionKeepsASessionThatStillValidates()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/web-interface/nav" => StubHttpHandler.Json(HasCookie(request, "SESSDATA=valid-token")
                ? LoggedInNav : AnonymousNav),
            "/x/player/wbi/playurl" => StubHttpHandler.Json("""{"code":-101,"message":"账号未登录"}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var account = await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            client.ResolveMediaAsync(new(Key, 101), TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.AuthenticationRequired, failure.Kind);
        Assert.Equal(account, client.CurrentAccount()); // A valid session is never cleared for gated content.
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/player/wbi/playurl"); // No guest retry.
    }

    [Fact]
    public async Task NetworkErrorDuringExpiryCheckKeepsTheSession()
    {
        var navCalls = 0;
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav")
            {
                navCalls++;
                return navCalls <= 2 ? StubHttpHandler.Json(LoggedInNav) : throw new HttpRequestException("offline");
            }
            if (request.RequestUri.AbsolutePath == "/x/player/wbi/playurl")
                return StubHttpHandler.Json("""{"code":-101,"message":"账号未登录"}""");
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri.AbsolutePath}");
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var account = await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            client.ResolveMediaAsync(new(Key, 101), TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.AuthenticationRequired, failure.Kind);
        Assert.Equal(account, client.CurrentAccount()); // A network blip never proves an expired session.
    }

    [Fact]
    public async Task SignOutClearsTheLocalSessionOnly()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(HasCookie(request, "SESSDATA=valid-token") ? LoggedInNav : AnonymousNav)
            : StubHttpHandler.Json(Media));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=valid-token", TestContext.Current.CancellationToken);
        var requestsBefore = handler.Requests.Count;
        BilibiliAccountChangedEventArgs? notification = null;
        client.AccountChanged += (_, args) => notification = args;

        await client.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.CurrentAccount());
        Assert.NotNull(notification);
        Assert.Null(notification!.Account);
        Assert.Equal(requestsBefore, handler.Requests.Count); // No all-site logout call is made.
        // Anonymous playback keeps working with the visitor credentials.
        var session = await client.ResolveMediaAsync(new(Key, 101), TestContext.Current.CancellationToken);
        Assert.NotNull(session.Audio);
    }
}
