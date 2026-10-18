using System.Text.Json;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliSearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Nav = """
        {"code":-101,"data":{"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    private const string Account = """
        {"code":0,"data":{"mid":42,"uname":"Viewer","isLogin":true,"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;

    [Fact]
    public async Task GuestSearchReturnsStableReferencesAndSanitizedTitlesWithoutResolvingPlayback()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("buvid3=visitor-three", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/x/web-interface/nav" => StubHttpHandler.Json(Nav),
                "/x/web-interface/wbi/search/type" => StubHttpHandler.Json(SearchPage(2, 2, 5,
                    Video("BV1GJ411x7h7", "<em class=\"keyword\">摄影</em> &amp; 光线", "03:12"))),
                _ => throw new InvalidOperationException("Search must not fetch video parts or media.")
            };
        });
        using var http = new HttpClient(handler);
        var result = await new BilibiliClient(http).SearchAsync("摄影 光线", page: 2, pageSize: 2,
            order: "pubdate", cancellationToken: Token);

        Assert.True(result.HasMore);
        Assert.Equal(5, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(new ContentRef("video", Bvid: "BV1GJ411x7h7"), item.Reference);
        Assert.Equal("摄影 & 光线", item.Title);
        Assert.Equal("Creator", item.Uploader);
        Assert.Equal(7, item.UploaderId);
        Assert.Equal(TimeSpan.FromSeconds(192), item.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000020), item.PublishedAt);
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/web-interface/wbi/search/type"));
        Assert.Equal("video", query["search_type"]);
        Assert.Equal("摄影 光线", query["keyword"]);
        Assert.Equal("pubdate", query["order"]);
        Assert.Equal("2", query["page"]);
        Assert.Equal("2", query["page_size"]);
        Assert.Equal(32, query["w_rid"].Length);
    }

    [Fact]
    public async Task SkipsPromotedCoursesWithoutInventingVideoIdentitiesOrLosingPagination()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav : SearchPage(1, 2, 3,
                Video("BV1GJ411x7h7", "Public tutorial", "03:12"),
                """{"type":"ketang","bvid":"","title":"Promoted course"}""")));
        using var http = new HttpClient(handler);

        var result = await new BilibiliClient(http).SearchAsync("test", pageSize: 2, cancellationToken: Token);

        Assert.Equal("BV1GJ411x7h7", Assert.Single(result.Items).Reference.Bvid);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(3, result.Total);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task CreatorSearchSupportsGuestSessionsAndUpstreamFanOrdering()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav : SearchPage(1, 20, 1,
                """{"mid":7,"uname":"<em>摄影</em>师 &amp; 编辑"}""")));
        using var http = new HttpClient(handler);
        var result = await new BilibiliClient(http).SearchAsync("摄影", "creator", order: "fans", cancellationToken: Token);

        var item = Assert.Single(result.Items);
        Assert.Equal(new ContentRef("creator", UploaderId: 7), item.Reference);
        Assert.Equal("摄影师 & 编辑", item.Title);
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/web-interface/wbi/search/type"));
        Assert.Equal("bili_user", query["search_type"]);
        Assert.Equal("fans", query["order"]);
    }

    [Fact]
    public async Task CreatorVideoBrowsingDoesNotRequireAnAccount()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav :
                BilibiliDiscoveryTests.VideoPage(1, 20, 1,
                    BilibiliDiscoveryTests.Video("BV1GJ411x7h7", 7, 1760000020, "1:02:03"))));
        using var http = new HttpClient(handler);
        var result = await new BilibiliClient(http).GetCreatorContentAsync(7, cancellationToken: Token);

        var item = Assert.Single(result.Items);
        Assert.Equal(new ContentRef("video", Bvid: "BV1GJ411x7h7"), item.Reference);
        Assert.Equal(TimeSpan.FromSeconds(3723), item.Duration);
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/space/wbi/arc/search"));
        Assert.Equal("7", query["mid"]);
        Assert.Equal("pubdate", query["order"]);
    }

    [Fact]
    public async Task CreatorCollectionsReturnsBothSeasonAndSeriesAsComposableReferences()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("/x/polymer/web-space/seasons_series_list", request.RequestUri!.AbsolutePath);
            return StubHttpHandler.Json("""
                {"code":0,"data":{"items_lists":{"page":{"page_num":2,"page_size":2,"total":5},
                  "seasons_list":[{"meta":{"mid":7,"season_id":11,"name":"摄影基础"}}],
                  "series_list":[{"meta":{"mid":7,"series_id":12,"name":"灯光练习"}}]}}}
                """);
        });
        using var http = new HttpClient(handler);
        var result = await new BilibiliClient(http).GetCreatorContentAsync(7, "collections", 2, 2, Token);

        Assert.True(result.HasMore);
        Assert.Equal(2, result.Page);
        Assert.Collection(result.Items,
            item => Assert.Equal(new ContentRef("collection", UploaderId: 7, CollectionId: 11,
                CollectionKind: CollectionKind.Season), item.Reference),
            item => Assert.Equal(new ContentRef("collection", UploaderId: 7, CollectionId: 12,
                CollectionKind: CollectionKind.Series), item.Reference));
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests,
            uri => uri.AbsolutePath == "/x/polymer/web-space/seasons_series_list"));
        Assert.Equal("7", query["mid"]);
        Assert.Equal("2", query["page_num"]);
        Assert.Equal("2", query["page_size"]);
    }

    [Theory]
    [InlineData("", "video", 1, 20, "relevance")]
    [InlineData("test", "collection", 1, 20, "relevance")]
    [InlineData("test", "video", 0, 20, "relevance")]
    [InlineData("test", "video", 1, 51, "relevance")]
    [InlineData("test", "video", 1, 20, "arbitrary")]
    public async Task RejectsInvalidSearchParametersBeforeNetwork(string query, string kind, int page, int size, string order)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).SearchAsync(query, kind, page, size, order, Token));
        Assert.Equal(ContentFailureKind.InvalidInput, failure.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, "videos")]
    [InlineData(-1, "collections")]
    [InlineData(7, "unsupported")]
    public async Task RejectsInvalidCreatorParametersBeforeNetwork(long uploaderId, string kind)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).GetCreatorContentAsync(uploaderId, kind, cancellationToken: Token));
        Assert.Equal(ContentFailureKind.InvalidInput, failure.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("invalid-bvid", "03:12")]
    [InlineData("BV1GJ411x7h7", "03:99")]
    [InlineData("BV1GJ411x7h7", "bad")]
    public async Task RejectsMalformedSearchIdentitiesAndDurations(string bvid, string duration)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav :
                SearchPage(1, 20, 1, Video(bvid, "Title", duration))));
        using var http = new HttpClient(handler);
        Assert.Equal(ContentFailureKind.InvalidResponse,
            (await Assert.ThrowsAsync<ContentAccessException>(() =>
                new BilibiliClient(http).SearchAsync("test", cancellationToken: Token))).Kind);
    }

    [Fact]
    public async Task RejectsMismatchedSearchPagination()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav : SearchPage(2, 20, 0)));
        using var http = new HttpClient(handler);
        Assert.Equal(ContentFailureKind.InvalidResponse,
            (await Assert.ThrowsAsync<ContentAccessException>(() =>
                new BilibiliClient(http).SearchAsync("test", cancellationToken: Token))).Kind);
    }

    [Theory]
    [InlineData("videos")]
    [InlineData("collections")]
    public async Task RejectsCreatorResultsWithAnUnexpectedOwner(string kind)
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav : kind == "videos"
                ? BilibiliDiscoveryTests.VideoPage(1, 20, 1,
                    BilibiliDiscoveryTests.Video("BV1GJ411x7h7", 8, 1760000020, "00:12"))
                : """{"code":0,"data":{"items_lists":{"page":{"page_num":1,"page_size":20,"total":1},"seasons_list":[{"meta":{"mid":8,"season_id":11,"name":"Wrong owner"}}],"series_list":[]}}}"""));
        using var http = new HttpClient(handler);
        Assert.Equal(ContentFailureKind.InvalidResponse,
            (await Assert.ThrowsAsync<ContentAccessException>(() =>
                new BilibiliClient(http).GetCreatorContentAsync(7, kind, cancellationToken: Token))).Kind);
    }

    [Fact]
    public async Task UsesAccountCredentialsWithoutRequiringVisitorProvisioning()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("SESSDATA=test-account", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/x/web-interface/nav" => StubHttpHandler.Json(Account),
                "/x/web-interface/wbi/search/type" => StubHttpHandler.Json(SearchPage(1, 20, 0)),
                _ => throw new InvalidOperationException("A signed-in search should not provision a visitor.")
            };
        }, initializeVisitor: false);
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);

        Assert.Empty((await client.SearchAsync("test", cancellationToken: Token)).Items);
    }

    [Fact]
    public async Task DiscardsPendingSearchAfterSignout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav") return Task.FromResult(StubHttpHandler.Json(Account));
            entered.TrySetResult();
            return release.Task;
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        await client.ImportCookiesAsync("SESSDATA=test-account", Token);
        var pending = client.SearchAsync("test", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await client.SignOutAsync(Token);
        release.SetResult(StubHttpHandler.Json(SearchPage(1, 20, 0)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ReportsRelatedNativeCollectionFromVideoMetadata()
    {
        using var handler = new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/x/web-interface/nav" ? Nav : """
                {"code":0,"data":{"bvid":"BV1GJ411x7h7","title":"Video","owner":{"mid":7,"name":"Creator"},
                  "duration":12,"pages":[{"page":1,"cid":101,"duration":12}],
                  "ugc_season":{"id":11,"mid":7,"title":"摄影基础"}}}
                """));
        using var http = new HttpClient(handler);
        var result = await new BilibiliClient(http).GetVideoAsync("BV1GJ411x7h7", Token);

        Assert.Equal(new CollectionSource(7, 11), Assert.Single(result.RelatedCollections!));
    }

    private static string Video(string bvid, string title, string duration) => JsonSerializer.Serialize(new
    { bvid, title, mid = 7, author = "Creator", pubdate = 1760000020, duration });

    private static string SearchPage(int page, int pageSize, int total, params string[] items) =>
        "{\"code\":0,\"data\":{\"page\":" + page + ",\"pagesize\":" + pageSize + ",\"numResults\":" + total
        + ",\"result\":[" + string.Join(',', items) + "]}}";

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
