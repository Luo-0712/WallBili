using System.Globalization;
using System.Text.Json;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliDiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Account = """
        {"code":0,"data":{"mid":42,"uname":"Viewer","isLogin":true,"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;

    [Fact]
    public async Task RequiresAnAccountWithoutSendingAnonymousDiscoveryRequests()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        Assert.Equal(ContentFailureKind.AuthenticationRequired,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetFollowingsAsync(cancellationToken: Token))).Kind);
        Assert.Equal(ContentFailureKind.AuthenticationRequired,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, cancellationToken: Token))).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadsOnlyTheCurrentAccountsFollowingPage()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("SESSDATA=test-account", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/x/web-interface/nav" => StubHttpHandler.Json(Account),
                "/x/relation/followings" => StubHttpHandler.Json("""
                    {"code":0,"data":{"total":5,"list":[{"mid":7,"uname":"Music"},{"mid":8,"uname":"Rain"}]}}
                    """),
                _ => throw new InvalidOperationException("Unexpected endpoint.")
            };
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        var result = await client.GetFollowingsAsync(2, 2, Token);

        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(5, result.Total);
        Assert.True(result.HasMore);
        Assert.Equal(new[] { new FollowedCreator(7, "Music"), new FollowedCreator(8, "Rain") }, result.Items);
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/relation/followings"));
        Assert.Equal("42", query["vmid"]);
        Assert.Equal("2", query["pn"]);
        Assert.Equal("2", query["ps"]);
    }

    [Fact]
    public async Task ReadsSignedPublicationOrderedVideoSummariesWithoutExpandingVideoParts()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/web-interface/nav" => StubHttpHandler.Json(Account),
            "/x/space/wbi/arc/search" => StubHttpHandler.Json(VideoPage(2, 2, 4,
                Video("BV1GJ411x7h7", 7, 1760000020, "03:12"),
                Video("BV1xx411c7mD", 7, 1760000010, "1:02:03"))),
            _ => throw new InvalidOperationException("Discovery must not fetch video parts or playback resources.")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        var result = await client.GetCreatorVideosAsync(7, 2, 2, Token);

        Assert.Equal(7, result.UploaderId);
        Assert.False(result.HasMore);
        Assert.Equal(TimeSpan.FromSeconds(192), result.Items[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(3723), result.Items[1].Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000020), result.Items[0].PublishedAt);
        Assert.All(result.Items, video => Assert.Equal("Creator", video.Uploader));
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/space/wbi/arc/search"));
        Assert.Equal("7", query["mid"]);
        Assert.Equal("pubdate", query["order"]);
        Assert.Equal("2", query["pn"]);
        Assert.Equal("2", query["ps"]);
        Assert.Equal(32, query["w_rid"].Length);
        Assert.Contains("wts", query.Keys);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public async Task RejectsInvalidPagingBeforeAnyNetworkRequest(int page, int size)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        Assert.Equal(ContentFailureKind.InvalidInput,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetFollowingsAsync(page, size, Token))).Kind);
        Assert.Equal(ContentFailureKind.InvalidInput,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, page, size, Token))).Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"list\":[]}")]
    [InlineData("{\"total\":-1,\"list\":[]}")]
    [InlineData("{\"total\":1,\"list\":null}")]
    [InlineData("{\"total\":1,\"list\":[{\"mid\":0,\"uname\":\"Creator\"}]}")]
    [InlineData("{\"total\":1,\"list\":[{\"mid\":7,\"uname\":\" \"}]}")]
    [InlineData("{\"total\":2,\"list\":[{\"mid\":7,\"uname\":\"A\"},{\"mid\":7,\"uname\":\"A\"}]}")]
    [InlineData("{\"total\":1,\"list\":[]}")]
    public async Task RejectsIncompleteOrInvalidFollowingPages(string data)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Account : "{\"code\":0,\"data\":" + data + "}"));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        var error = await Assert.ThrowsAsync<ContentAccessException>(() => client.GetFollowingsAsync(cancellationToken: Token));

        Assert.Equal(ContentFailureKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData(8, "BV1GJ411x7h7", 1760000020, "03:12")]
    [InlineData(7, "invalid-bvid", 1760000020, "03:12")]
    [InlineData(7, "BV1GJ411x7h7", 0, "03:12")]
    [InlineData(7, "BV1GJ411x7h7", 1760000020, "not-a-duration")]
    [InlineData(7, "BV1GJ411x7h7", 1760000020, "03:99")]
    public async Task RejectsInvalidVideoIdentityDateAndDuration(long returnedUid, string bvid, long published, string duration)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Account : VideoPage(1, 30, 1,
                Video(bvid, returnedUid, published, duration))));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        var error = await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, cancellationToken: Token));

        Assert.Equal(ContentFailureKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData(2, 30)]
    [InlineData(1, 50)]
    public async Task RejectsVideoPageThatDoesNotMatchTheRequestedPagination(int returnedPage, int returnedSize)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Account : VideoPage(returnedPage, returnedSize, 0)));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        Assert.Equal(ContentFailureKind.InvalidResponse,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, cancellationToken: Token))).Kind);
    }

    [Fact]
    public async Task RejectsOutOfOrderPublicationPagesRatherThanClaimingACompleteIncrementalScan()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Account : VideoPage(1, 30, 2,
                Video("BV1GJ411x7h7", 7, 1760000010, "00:12"), Video("BV1xx411c7mD", 7, 1760000020, "00:22"))));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        Assert.Equal(ContentFailureKind.InvalidResponse,
            (await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, cancellationToken: Token))).Kind);
    }

    [Theory]
    [InlineData(-101, ContentFailureKind.AuthenticationRequired)]
    [InlineData(-352, ContentFailureKind.RateLimited)]
    [InlineData(-404, ContentFailureKind.Removed)]
    public async Task PreservesSafeServiceFailureClassification(int code, ContentFailureKind expected)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Account
                : "{\"code\":" + code.ToString(CultureInfo.InvariantCulture) + ",\"message\":\"SESSDATA=private\"}"));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        var error = await Assert.ThrowsAsync<ContentAccessException>(() => client.GetCreatorVideosAsync(7, cancellationToken: Token));

        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain("private", error.ToString());
        Assert.NotNull(client.CurrentAccount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardsResponsesAfterSignoutOrAccountReplacement(bool replaceAccount)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav")
                return Task.FromResult(StubHttpHandler.Json(request.Headers.GetValues("Cookie").Contains("SESSDATA=second-account")
                    ? Account.Replace("\"mid\":42", "\"mid\":84", StringComparison.Ordinal) : Account));
            entered.TrySetResult();
            return release.Task;
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);
        var pending = client.GetFollowingsAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (replaceAccount) await client.ImportCookiesAsync("SESSDATA=second-account", Token);
        else await client.SignOutAsync(Token);
        release.SetResult(StubHttpHandler.Json("""{"code":0,"data":{"total":0,"list":[]}}"""));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    internal static string Video(string bvid, long uid, long created, string length) => JsonSerializer.Serialize(new
    { bvid, mid = uid, author = "Creator", title = "Background video", created, length });

    internal static string VideoPage(int page, int size, int total, params string[] videos) =>
        "{\"code\":0,\"data\":{\"page\":{\"pn\":" + page + ",\"ps\":" + size + ",\"count\":" + total
        + "},\"list\":{\"vlist\":[" + string.Join(',', videos) + "]}}}";

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
