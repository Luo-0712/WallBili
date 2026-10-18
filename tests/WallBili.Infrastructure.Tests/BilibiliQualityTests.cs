using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliQualityTests
{
    private const string Nav = """
        {"code":-101,"data":{"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    private const string Key = "BV1GJ411x7h7";

    private static async Task<MediaSession> ResolveAsync(string payload, MediaSelectionPreferences? selection = null)
    {
        using var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath == "/x/web-interface/nav"
            ? StubHttpHandler.Json(Nav) : StubHttpHandler.Json(payload));
        using var http = new HttpClient(handler);
        return await new BilibiliClient(http).ResolveMediaAsync(new(Key, 101), selection,
            TestContext.Current.CancellationToken);
    }

    private const string Formats = """
        "support_formats":[
            {"quality":120,"new_desc":"4K 超清"},{"quality":80,"new_desc":"1080P 高清"},
            {"quality":64,"new_desc":"720P 高清"},{"quality":32,"new_desc":"480P 清晰"},
            {"quality":16,"new_desc":"360P 流畅"}],
        """;

    private static string Dash(string video, string audio, string extras = "") =>
        "{\"code\":0,\"data\":{" + Formats + "\"dash\":{" + video + audio + extras + "}}}";

    private const string NormalAudio = """
        "audio":[
          {"id":30280,"baseUrl":"https://media.example/audio-192","codecs":"mp4a.40.2","bandwidth":192000},
          {"id":30216,"baseUrl":"https://media.example/audio-64","codecs":"mp4a.40.2","bandwidth":64000}]
        """;

    [Fact]
    public async Task DeclaredButMissingTiersAreNeverListed()
    {
        var media = await ResolveAsync(Dash("""
            "video":[{"id":32,"baseUrl":"https://media.example/video-480","codecs":"avc1.64001F",
              "width":854,"height":480,"bandwidth":800000}],
            """, NormalAudio));

        var option = Assert.Single(media.Selection!.VideoOptions);
        Assert.Equal("32", option.Id);
        Assert.Equal("480P 清晰", option.Label);
        Assert.Equal("480P 清晰（AVC）", media.Selection.SelectedVideo);
        Assert.Equal("https://media.example/video-480", media.Video.Url.AbsoluteUri);
    }

    [Fact]
    public async Task AutomaticVideoPicksHighestAvailableTierNotTheDeclaredList()
    {
        var media = await ResolveAsync(Dash("""
            "video":[
              {"id":80,"baseUrl":"https://media.example/video-1080","codecs":"avc1.640028",
                "width":1920,"height":1080,"bandwidth":3000000},
              {"id":32,"baseUrl":"https://media.example/video-480","codecs":"avc1.64001F",
                "width":854,"height":480,"bandwidth":800000}],
            """, NormalAudio));

        Assert.Equal("80", Assert.Single(media.Selection!.VideoOptions, o => o.Label == "1080P 高清").Id);
        Assert.Equal("1080P 高清（AVC）", media.Selection.SelectedVideo);
        Assert.Equal("https://media.example/video-1080", media.Video.Url.AbsoluteUri);
    }

    [Fact]
    public async Task PreferredMissingVideoTierFallsBackWithinRules()
    {
        var payload = Dash("""
            "video":[
              {"id":80,"baseUrl":"https://media.example/video-1080","codecs":"avc1.640028",
                "width":1920,"height":1080,"bandwidth":3000000},
              {"id":32,"baseUrl":"https://media.example/video-480","codecs":"avc1.64001F",
                "width":854,"height":480,"bandwidth":800000}],
            """, NormalAudio);

        // Not above the target: 120 is unavailable, so 80 is the highest tier not above 120.
        var lower = await ResolveAsync(payload, new(VideoQualityId: "120"));
        Assert.Equal("1080P 高清（AVC）", lower.Selection!.SelectedVideo);
        Assert.Contains("已回退", lower.Selection.VideoFallback);

        // Nothing below the target: the lowest available tier is used.
        var lowest = await ResolveAsync(payload, new(VideoQualityId: "16"));
        Assert.Equal(32, lowest.Video.Quality);
        Assert.Contains("已回退", lowest.Selection!.VideoFallback);

        // Exact tiers are honoured without a fallback note.
        var exact = await ResolveAsync(payload, new(VideoQualityId: "32"));
        Assert.Equal(32, exact.Video.Quality);
        Assert.Null(exact.Selection!.VideoFallback);
    }

    [Fact]
    public async Task MultipleCodecsOfOneTierMergeAndPreferAvc()
    {
        var media = await ResolveAsync(Dash("""
            "video":[
              {"id":80,"baseUrl":"https://media.example/video-hevc","codecs":"hev1.1.6.L120",
                "width":1920,"height":1080,"bandwidth":2000000},
              {"id":80,"baseUrl":"https://media.example/video-av1","codecs":"av01.0.08M.08",
                "width":1920,"height":1080,"bandwidth":1800000},
              {"id":80,"baseUrl":"https://media.example/video-avc","codecs":"avc1.640028",
                "width":1920,"height":1080,"bandwidth":4000000}],
            """, NormalAudio));

        Assert.Equal(new[] { "80" }, media.Selection!.VideoOptions.Select(o => o.Id).ToArray());
        Assert.Equal("https://media.example/video-avc", media.Video.Url.AbsoluteUri); // AVC > HEVC > AV1.
    }

    [Fact]
    public async Task HevcIsPreferredOverAv1WhenAvcIsAbsent()
    {
        var media = await ResolveAsync(Dash("""
            "video":[
              {"id":80,"baseUrl":"https://media.example/video-av1","codecs":"av01.0.08M.08",
                "width":1920,"height":1080,"bandwidth":1800000},
              {"id":80,"baseUrl":"https://media.example/video-hevc","codecs":"hvc1.1.6.L120",
                "width":1920,"height":1080,"bandwidth":2000000}],
            """, NormalAudio));

        Assert.Equal("https://media.example/video-hevc", media.Video.Url.AbsoluteUri);
        Assert.Equal("1080P 高清（HEVC）", media.Selection!.SelectedVideo);
    }

    [Fact]
    public async Task UnknownQualityIdIsKeptAndLabelledFromStreamParameters()
    {
        var media = await ResolveAsync(Dash("""
            "video":[
              {"id":999,"baseUrl":"https://media.example/video-unknown","codecs":"avc1.640028",
                "width":1600,"height":900,"bandwidth":2500000}],
            """, NormalAudio));

        var option = Assert.Single(media.Selection!.VideoOptions);
        Assert.Equal("999", option.Id);
        Assert.Contains("999", option.Label);
        Assert.Equal(999, media.Video.Quality); // Never dropped just because the id is unlisted.
    }

    [Fact]
    public async Task AutomaticAudioPicksHighestNormalTierAndLabelsBandwidth()
    {
        var media = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, NormalAudio));

        Assert.Equal(new[] { "30280", "30216" }, media.Selection!.AudioOptions.Select(o => o.Id).ToArray());
        Assert.Equal("192 kbps（AAC）", media.Selection.AudioOptions[0].Label);
        Assert.Equal("192 kbps（AAC）", media.Selection.SelectedAudio);
        Assert.Equal("https://media.example/audio-192", media.Audio!.Url.AbsoluteUri);
        Assert.All(media.Selection.AudioOptions, o => Assert.False(o.Special));
    }

    [Fact]
    public async Task DolbyAndLosslessTiersAppearOnlyWhenTheirStreamsAreReturned()
    {
        const string specialAudio = """
            "audio":[
              {"id":30280,"baseUrl":"https://media.example/audio-192","codecs":"mp4a.40.2","bandwidth":192000}],
            "dolby":{"audio":[
              {"id":30250,"baseUrl":"https://media.example/audio-dolby","codecs":"ec-3.1.1","bandwidth":448000}]},
            "flac":{"display":true,"audio":
              {"id":30251,"baseUrl":"https://media.example/audio-flac","codecs":"fLaC","bandwidth":900000}}
            """;

        var media = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, specialAudio));

        Assert.Equal(new[] { "30280", "30250", "30251" }, media.Selection!.AudioOptions.Select(o => o.Id).ToArray());
        Assert.All(media.Selection.AudioOptions.Skip(1), o => Assert.True(o.Special));
        Assert.Equal("192 kbps（AAC）", media.Selection.SelectedAudio); // Automatic mode stays on normal tiers.

        var dolby = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, specialAudio), new(AudioQualityId: "30250"));
        Assert.Equal("杜比全景声", dolby.Selection!.SelectedAudio);
        Assert.Equal("https://media.example/audio-dolby", dolby.Audio!.Url.AbsoluteUri);

        var flac = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, specialAudio), new(AudioQualityId: "30251"));
        Assert.Equal("Hi-Res 无损", flac.Selection!.SelectedAudio);
        Assert.Equal("https://media.example/audio-flac", flac.Audio!.Url.AbsoluteUri);
    }

    [Fact]
    public async Task MissingSpecialAudioFallsBackToHighestNormalWithAReason()
    {
        var media = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, NormalAudio), new(AudioQualityId: "30251"));

        Assert.Equal("https://media.example/audio-192", media.Audio!.Url.AbsoluteUri); // Highest normal tier.
        Assert.Equal("192 kbps（AAC）", media.Selection!.SelectedAudio);
        Assert.Contains("回退", media.Selection.AudioFallback);
    }

    [Theory]
    [InlineData("30232", "30216")] // Highest tier not above the missing target id.
    [InlineData("999999", "30280")] // Above everything resolves to the highest tier.
    [InlineData("1", "30216")] // Below everything resolves to the lowest tier.
    public async Task MissingNormalAudioFallsBackByTierId(string preferred, int expectedQuality)
    {
        var media = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, NormalAudio), new(AudioQualityId: preferred));

        Assert.Equal(expectedQuality, media.Audio!.Quality);
    }

    [Fact]
    public async Task QualitySummaryContainsNoMediaAddresses()
    {
        var media = await ResolveAsync(Dash("""
            "video":[{"id":80,"baseUrl":"https://media.example/video-private?token=secret","codecs":"avc1.640028",
              "width":1920,"height":1080,"bandwidth":3000000}],
            """, """
            "audio":[{"id":30280,"baseUrl":"https://media.example/audio-private?token=secret","codecs":"mp4a.40.2","bandwidth":192000}]
            """));

        Assert.DoesNotContain("secret", media.Selection!.ToString());
        Assert.DoesNotContain("media.example", media.Selection.ToString());
    }
}
