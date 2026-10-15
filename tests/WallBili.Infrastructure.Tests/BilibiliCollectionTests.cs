using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliCollectionTests
{
    [Fact]
    public async Task ReadsSeasonPageAndPreservesStableVideoIdentities()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("/x/polymer/web-space/seasons_archives_list", request.RequestUri!.AbsolutePath);
            var query = StubHttpHandler.Query(request.RequestUri);
            Assert.Equal("42", query["mid"]);
            Assert.Equal("7", query["season_id"]);
            Assert.Equal("2", query["page_num"]);
            Assert.Equal("2", query["page_size"]);
            return StubHttpHandler.Json("""
                {"code":0,"data":{"meta":{"name":"Saved season"},
                  "page":{"page_num":2,"page_size":2,"total":5},
                  "archives":[{"bvid":"BV1GJ411x7h7","title":"Third video","duration":61},
                    {"bvid":"BV17x411w7KC","title":"Fourth video","duration":62}]}}
                """);
        });
        using var http = new HttpClient(handler);
        var source = new CollectionSource(42, 7, CollectionKind.Season);

        var page = await new BilibiliClient(http).GetCollectionPageAsync(source, page: 2, pageSize: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(source, page.Source);
        Assert.Equal("Saved season", page.Title);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(5, page.Total);
        Assert.True(page.HasMore);
        Assert.Collection(page.Entries,
            item => Assert.Equal(new CollectionEntry("BV1GJ411x7h7", "Third video", TimeSpan.FromSeconds(61)), item),
            item => Assert.Equal(new CollectionEntry("BV17x411w7KC", "Fourth video", TimeSpan.FromSeconds(62)), item));
    }

    [Fact]
    public async Task ReadsSeriesLastPageWithoutTreatingListingAsPartMetadata()
    {
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/series/series")
                return StubHttpHandler.Json("""{"code":0,"data":{"meta":{"name":"Saved series"}}}""");
            Assert.Equal("/x/series/archives", request.RequestUri.AbsolutePath);
            var query = StubHttpHandler.Query(request.RequestUri);
            Assert.Equal("42", query["mid"]);
            Assert.Equal("8", query["series_id"]);
            Assert.Equal("3", query["pn"]);
            Assert.Equal("2", query["ps"]);
            return StubHttpHandler.Json("""
                {"code":0,"data":{"page":{"num":3,"size":2,"total":5},
                  "archives":[{"bvid":"BV1GJ411x7h7","title":"Final video","duration":61}]}}
                """);
        });
        using var http = new HttpClient(handler);
        var source = new CollectionSource(42, 8, CollectionKind.Series);

        var page = await new BilibiliClient(http).GetCollectionPageAsync(source, page: 3, pageSize: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(source, page.Source);
        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.Page);
        Assert.False(page.HasMore);
        Assert.Equal("BV1GJ411x7h7", Assert.Single(page.Entries).Bvid);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath is "/x/player/pagelist" or "/x/player/wbi/playurl");
    }

    [Fact]
    public async Task UsesServerCappedPageSizeToAvoidSkippingRemainingEntries()
    {
        using var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""
            {"code":0,"data":{"page":{"page_num":1,"page_size":2,"total":5},
              "archives":[{"bvid":"BV1GJ411x7h7","title":"First","duration":1},
                {"bvid":"BV17x411w7KC","title":"Second","duration":2}]}}
            """));
        using var http = new HttpClient(handler);

        var page = await new BilibiliClient(http).GetCollectionPageAsync(new(42, 7), pageSize: 30, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, page.PageSize);
        Assert.True(page.HasMore);
    }

    [Theory]
    [InlineData("{\"page_num\":1,\"page_size\":30}")]
    [InlineData("{\"page_num\":1,\"page_size\":30,\"total\":2.5}")]
    [InlineData("{\"page_num\":1,\"page_size\":30,\"total\":-1}")]
    [InlineData("{\"page_num\":1,\"page_size\":30,\"total\":2147483648}")]
    [InlineData("{\"page_num\":2,\"page_size\":30,\"total\":5}")]
    public async Task RejectsMalformedOrMismatchedPagination(string pagination)
    {
        using var handler = new StubHttpHandler(_ => StubHttpHandler.Json(
            $$$$"""{"code":0,"data":{"page":{{{{pagination}}}},"archives":[]}}"""));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).GetCollectionPageAsync(new(42, 7), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.InvalidResponse, failure.Kind);
    }

    [Theory]
    [InlineData(0, 7, 1, 30)]
    [InlineData(42, 0, 1, 30)]
    [InlineData(42, 7, 0, 30)]
    [InlineData(42, 7, 1, 0)]
    [InlineData(42, 7, 1, 101)]
    public async Task RejectsInvalidSourceOrPaginationBeforeNetwork(long uploaderId, long collectionId, int page, int pageSize)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No network request expected."));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).GetCollectionPageAsync(new(uploaderId, collectionId), page, pageSize, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.InvalidInput, failure.Kind);
        Assert.Empty(handler.Requests);
    }
}
