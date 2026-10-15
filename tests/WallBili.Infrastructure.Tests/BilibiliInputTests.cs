using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliInputTests
{
    [Theory]
    [InlineData("BV1GJ411x7h7", 1)]
    [InlineData("  BV1GJ411x7h7  ", 1)]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=2&share_source=copy_web", 2)]
    [InlineData("https://bilibili.com/video/BV1GJ411x7h7/", 1)]
    [InlineData("https://m.bilibili.com/video/BV1GJ411x7h7?%70=3", 3)]
    public void ParsesStableVideoIdentityAndPart(string value, int page)
    {
        var input = BilibiliInput.Parse(value);

        Assert.Equal("BV1GJ411x7h7", input.Bvid);
        Assert.Equal(page, input.Page);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("BV123")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=0")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=-1")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=abc")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=2147483648")]
    [InlineData("https://name@www.bilibili.com/video/BV1GJ411x7h7")]
    [InlineData("ftp://www.bilibili.com/video/BV1GJ411x7h7")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7/unrelated")]
    public void RejectsInvalidIdentityOrPart(string value)
    {
        var failure = Assert.Throws<ContentAccessException>(() => BilibiliInput.Parse(value));

        Assert.Equal(ContentFailureKind.InvalidInput, failure.Kind);
    }

    [Theory]
    [InlineData("https://www.bilibili.com.evil.example/video/BV1GJ411x7h7")]
    [InlineData("https://evil.example/video/BV1GJ411x7h7")]
    public void RejectsLinksOutsideSupportedHosts(string value)
    {
        var failure = Assert.Throws<ContentAccessException>(() => BilibiliInput.Parse(value));

        Assert.Equal(ContentFailureKind.Unsupported, failure.Kind);
    }
}
