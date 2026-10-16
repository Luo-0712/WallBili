using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliSessionTests
{
    private const string Nav = """
        {"code":-101,"data":{"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    private const string Mp4 = """
        {"code":0,"data":{"format":"mp4","quality":16,"durl":[{"url":"https://media.example/combined.mp4"}]}}
        """;
    private const string Dash = """
        {"code":0,"data":{"dash":{
          "video":[{"id":16,"baseUrl":"https://media.example/video","codecs":"avc1.64001E","height":360}],
          "audio":[{"id":30280,"baseUrl":"https://media.example/audio","codecs":"mp4a.40.2"}]}}}
        """;

    [Fact]
    public async Task CachesVisitorAndSigningMaterialWhileResolvingEveryMediaRequest()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav) : StubHttpHandler.Json(Mp4));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(8, sessions.Length);
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/frontend/finger/spi");
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/web-interface/nav");
        Assert.Equal(8, handler.Requests.Count(uri => uri.AbsolutePath == "/x/player/wbi/playurl"));
        Assert.All(sessions, session => Assert.True(session.Selection!.EmbeddedAudio));
        Assert.All(handler.Requests.Where(uri => uri.AbsolutePath == "/x/player/wbi/playurl"),
            uri => Assert.Equal("4048", StubHttpHandler.Query(uri)["fnval"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignatureRejectionRefreshesMaterialOnceWithBoundedRetry(bool succeedsAfterRefresh)
    {
        var calls = 0;
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json(++calls == 1 || !succeedsAfterRefresh ? """{"code":-403}""" : Mp4));
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        if (succeedsAfterRefresh)
            Assert.True((await client.ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken)).Selection!.EmbeddedAudio);
        else
            Assert.Equal(ContentFailureKind.Inaccessible,
                (await Assert.ThrowsAsync<ContentAccessException>(() => client.ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken))).Kind);
        Assert.Equal(2, calls);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath == "/x/web-interface/nav"));
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/frontend/finger/spi");
    }

    [Fact]
    public async Task AcceptsProgressiveMp4CompatPathWithoutExtraRequests()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav) : StubHttpHandler.Json(Mp4));
        using var http = new HttpClient(handler);
        var session = await new BilibiliClient(http).ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(session.Selection!.EmbeddedAudio);
        Assert.Equal(1, handler.Requests.Count(uri => uri.AbsolutePath == "/x/player/wbi/playurl"));
        Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/web-interface/nav");
    }

    [Fact]
    public async Task RejectsMultiSegmentProgressiveResponse()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json("""{"code":0,"data":{"format":"flv","quality":16,"durl":[{},{}]}}"""));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.Unsupported, failure.Kind);
        Assert.Equal(1, handler.Requests.Count(uri => uri.AbsolutePath == "/x/player/wbi/playurl")); // No format retry loops.
    }

    [Fact]
    public async Task RejectsInvalidVisitorCredentialsWithoutLoggingTheirValues()
    {
        using var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""
            {"code":0,"data":{"b_3":"private-cookie; injected=secret","b_4":"visitor-four"}}
            """), initializeVisitor: false);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).GetVideoAsync("BV17x411w7KC", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ContentFailureKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain("private-cookie", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AutomaticAudioPicksHighestNormalTier()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav) : StubHttpHandler.Json(Dash));
        using var http = new HttpClient(handler);
        var session = await new BilibiliClient(http).ResolveMediaAsync(new("BV17x411w7KC", 101), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://media.example/audio", session.Audio!.Url.AbsoluteUri);
        Assert.False(session.Selection!.NoAudio);
    }
}
