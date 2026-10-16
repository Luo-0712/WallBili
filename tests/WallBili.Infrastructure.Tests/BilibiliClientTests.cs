using System.Net;
using System.Security.Cryptography;
using System.Text;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliClientTests
{
    private const string Bvid = "BV1GJ411x7h7";
    private const string Nav = """
        {"code":-101,"message":"not logged in","data":{"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;

    [Fact]
    public async Task UsesSignedMetadataWithGuestCredentialsAndAllParts()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("buvid3=visitor-three", request.Headers.GetValues("Cookie").Single());
            return request.RequestUri!.AbsolutePath switch
            {
                "/x/web-interface/nav" => StubHttpHandler.Json(Nav),
                "/x/web-interface/wbi/view" => StubHttpHandler.Json("""
                    {"code":0,"data":{"bvid":"BV1GJ411x7h7","title":"A multi-part video",
                      "owner":{"name":"Uploader","mid":42},"duration":125,
                      "pages":[{"cid":101,"page":1,"part":"Part one","duration":60},
                        {"cid":202,"page":2,"part":"Part two","duration":65}]}}
                    """),
                _ => throw new InvalidOperationException("Unexpected endpoint.")
            };
        });
        using var http = new HttpClient(handler);
        var video = await new BilibiliClient(http).GetVideoAsync($"https://www.bilibili.com/video/{Bvid}?p=2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Bvid, video.Bvid);
        Assert.Equal("A multi-part video", video.Title);
        Assert.Equal("Uploader", video.Uploader);
        Assert.Equal(42, video.UploaderId);
        Assert.Collection(video.Parts,
            part => Assert.Equal(new VideoPart(1, new(Bvid, 101), "Part one", TimeSpan.FromSeconds(60)), part),
            part => Assert.Equal(new VideoPart(2, new(Bvid, 202), "Part two", TimeSpan.FromSeconds(65)), part));
        var query = StubHttpHandler.Query(Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/web-interface/wbi/view"));
        Assert.Equal(Bvid, query["bvid"]);
        Assert.Equal(32, query["w_rid"].Length);
        Assert.Contains("wts", query.Keys);
        Assert.DoesNotContain(handler.Requests, uri => uri.Host != "api.bilibili.com" || uri.AbsolutePath == "/x/web-interface/view");
    }

    [Fact]
    public async Task ResolvesDashWithSignedParametersAndAutomaticHighestTier()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/web-interface/nav" => StubHttpHandler.Json(Nav),
            "/x/player/wbi/playurl" => StubHttpHandler.Json(Dash("first")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri!.AbsolutePath}")
        });
        using var http = new HttpClient(handler);

        // Automatic mode picks the highest tier actually returned, without an artificial 1080P cap.
        var media = await new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 202), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ContentKey(Bvid, 202), media.Content);
        Assert.Equal("https://media.example/video-4k-first", media.Video.Url.AbsoluteUri);
        Assert.StartsWith("avc1", media.Video.Codec);
        Assert.Equal(120, media.Video.Quality);
        Assert.Equal("https://media.example/audio-high-first?deadline=2000000000", media.Audio!.Url.AbsoluteUri);
        Assert.Equal("4K 超清（AVC）", media.Selection!.SelectedVideo);
        Assert.Equal("192 kbps（AAC）", media.Selection.SelectedAudio);
        Assert.Equal(new[] { "120", "80", "64" }, media.Selection.VideoOptions.Select(o => o.Id).ToArray());
        Assert.Equal(new[] { "30280", "30216" }, media.Selection.AudioOptions.Select(o => o.Id).ToArray());
        Assert.Contains(media.Headers, pair => pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
            && pair.Value.Contains("bilibili.com", StringComparison.Ordinal));
        Assert.Contains(media.Headers, pair => pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(pair.Value));

        var query = StubHttpHandler.Query(Assert.Single(handler.Requests, uri => uri.AbsolutePath == "/x/player/wbi/playurl"));
        Assert.Equal(Bvid, query["bvid"]);
        Assert.Equal("202", query["cid"]);
        Assert.DoesNotContain("qn", query.Keys); // No fixed quality cap is requested anymore.
        Assert.Equal("4048", query["fnval"]);
        Assert.Equal("0", query["fnver"]);
        Assert.Equal("1", query["fourk"]);
        Assert.Equal("pc", query["platform"]);
        Assert.True(long.TryParse(query["wts"], out _));
        var canonical = string.Join('&', query.Where(pair => pair.Key != "w_rid")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        // Independent published fixture: these nav filenames yield this WBI mixin key.
        var expectedSignature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(
            canonical + "ea1db124af3c7062474693fa704f4ff8")));
        Assert.Equal(expectedSignature, query["w_rid"]);
    }

    [Fact]
    public async Task ResolvesFreshMediaForEveryPlaybackAttempt()
    {
        var playbackCalls = 0;
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/x/web-interface/nav" => StubHttpHandler.Json(Nav),
            "/x/player/wbi/playurl" => StubHttpHandler.Json(Dash((++playbackCalls).ToString())),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri!.AbsolutePath}")
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);

        var first = await client.ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken);
        var retry = await client.ResolveMediaAsync(first.Content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, playbackCalls);
        Assert.NotEqual(first.Video.Url, retry.Video.Url);
        Assert.Equal(first.Content, retry.Content);
    }

    [Theory]
    [InlineData(-404, ContentFailureKind.Removed)]
    [InlineData(-101, ContentFailureKind.AuthenticationRequired)]
    [InlineData(-403, ContentFailureKind.Inaccessible)]
    [InlineData(-509, ContentFailureKind.RateLimited)]
    public async Task ClassifiesServiceFailuresWithoutExposingResponseBody(int code, ContentFailureKind expected)
    {
        const string secret = "https://media.example/private?access_token=server-secret";
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json($$$$"""{"code":{{{{code}}}},"message":"{{{{secret}}}}"}"""));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected, failure.Kind);
        Assert.Equal(code, failure.ServiceCode);
        Assert.DoesNotContain("server-secret", failure.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ContentFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, ContentFailureKind.Removed)]
    [InlineData(HttpStatusCode.Forbidden, ContentFailureKind.Inaccessible)]
    public async Task ClassifiesHttpFailures(HttpStatusCode status, ContentFailureKind expected)
    {
        using var handler = new StubHttpHandler(_ => StubHttpHandler.Json("private response", status), initializeVisitor: false);
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected, failure.Kind);
    }

    [Fact]
    public async Task ClassifiesNetworkFailuresWithoutExposingTransportDetails()
    {
        using var handler = new StubHttpHandler(_ => throw new HttpRequestException("secret-token=do-not-log"), initializeVisitor: false);
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.Network, failure.Kind);
        Assert.DoesNotContain("do-not-log", failure.ToString());
    }

    [Fact]
    public async Task RejectsMalformedJsonAsInvalidResponse()
    {
        using var handler = new StubHttpHandler(_ => StubHttpHandler.Json("<html>unexpected response</html>"), initializeVisitor: false);
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.InvalidResponse, failure.Kind);
    }

    [Fact]
    public async Task PreservesCallerCancellation()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No network request expected."));
        using var http = new HttpClient(handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancelled.Token));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public async Task RejectsRestrictedPreviewAsAuthenticationRequired(string previewValue)
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json($$$$"""{"code":0,"data":{"is_preview":{{{{previewValue}}}}}}"""));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.AuthenticationRequired, failure.Kind);
    }

    [Fact]
    public async Task RejectsDashWithoutVideoStream()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json("""
                {"code":0,"data":{"dash":{
                  "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"unknown-codec","height":1080}],
                  "audio":[{"id":30280,"baseUrl":"https://media.example/audio","codecs":"mp4a.40.2"}]}}}
                """));
        using var http = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContentAccessException>(() =>
            new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ContentFailureKind.Unsupported, failure.Kind);
    }

    [Fact]
    public async Task AcceptsVideoOnlyDashWhenNoAudioStreamIsReturned()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json("""
                {"code":0,"data":{"dash":{
                  "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028","width":1920,"height":1080,"bandwidth":4000000}]}}}
                """));
        using var http = new HttpClient(handler);

        var media = await new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(media.Audio);
        Assert.True(media.Selection!.NoAudio);
        Assert.Equal("无音轨", media.Selection.SelectedAudio);
        Assert.Empty(media.Selection.AudioOptions);
    }

    [Fact]
    public async Task AcceptsSingleProgressiveMp4WithEmbeddedAudio()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json("""
                {"code":0,"data":{"quality":64,"format":"mp4","support_formats":[{"quality":64,"new_desc":"720P 高清"}],
                  "durl":[{"url":"https://media.example/combined.mp4"}]}}
                """));
        using var http = new HttpClient(handler);

        var media = await new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://media.example/combined.mp4", media.Video.Url.AbsoluteUri);
        Assert.Null(media.Audio);
        Assert.True(media.Selection!.EmbeddedAudio);
        Assert.Equal("720P 高清", media.Selection.SelectedVideo);
        Assert.Equal("内嵌音轨，不可单独选择", media.Selection.SelectedAudio);
        Assert.Single(media.Selection.VideoOptions); // Only the actually provided quality is listed.
        Assert.Empty(media.Selection.AudioOptions);
    }

    [Fact]
    public async Task SessionDiagnosticsDoNotIncludeSignedMediaUrls()
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav)
            : StubHttpHandler.Json(Dash("private-token")));
        using var http = new HttpClient(handler);

        var media = await new BilibiliClient(http).ResolveMediaAsync(new(Bvid, 101), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("private-token", media.ToString());
        Assert.DoesNotContain("media.example", media.ToString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), media.ExpiresAt);
    }

    private static string Dash(string suffix) => $$$$"""
        {"code":0,"data":{"quality":120,"timelength":60000,"support_formats":[
            {"quality":120,"new_desc":"4K 超清"},{"quality":80,"new_desc":"1080P 高清"},{"quality":64,"new_desc":"720P 高清"}],
          "dash":{
          "video":[
            {"id":120,"baseUrl":"https://media.example/video-4k-{{{{suffix}}}}","codecs":"avc1.640033","width":3840,"height":2160,"bandwidth":8000000},
            {"id":80,"baseUrl":"https://media.example/video-hevc-{{{{suffix}}}}","codecs":"hev1.1.6.L120","width":1920,"height":1080,"bandwidth":2000000},
            {"id":64,"baseUrl":"https://media.example/video-720-{{{{suffix}}}}","codecs":"avc1.64001F","width":1280,"height":720,"bandwidth":1500000},
            {"id":80,"baseUrl":"https://media.example/video-{{{{suffix}}}}?deadline=2000000000","codecs":"avc1.640028","width":1920,"height":1080,"bandwidth":4000000}],
          "audio":[
            {"id":30216,"baseUrl":"https://media.example/audio-low-{{{{suffix}}}}","codecs":"mp4a.40.2","bandwidth":64000},
            {"id":30280,"baseUrl":"https://media.example/audio-high-{{{{suffix}}}}?deadline=2000000000","codecs":"mp4a.40.2","bandwidth":192000}]}}}
        """;
}
