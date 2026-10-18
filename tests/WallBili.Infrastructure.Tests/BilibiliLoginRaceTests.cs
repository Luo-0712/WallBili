using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class BilibiliLoginRaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Account = """{"code":0,"data":{"mid":42,"uname":"Uploader","isLogin":true}}""";

    [Theory]
    [InlineData(0)] // Reissue.
    [InlineData(1)] // Sign out while still anonymous.
    [InlineData(2)] // Cancel the dialog.
    [InlineData(3)] // Switch to Cookie import.
    public async Task QrSuccessCannotApplyAfterSessionChangeDuringNav(int change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = 0;
        using var handler = new AsyncHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/x/passport-login/web/qrcode/generate" => Task.FromResult(Generate(++generations)),
            "/x/passport-login/web/qrcode/poll" => Task.FromResult(QrSuccess()),
            "/x/web-interface/nav" => request.Headers.GetValues("Cookie").Contains("SESSDATA=qr-token")
                ? WaitForNav() : Task.FromResult(StubHttpHandler.Json(Account.Replace("42", "84", StringComparison.Ordinal))),
            _ => throw new InvalidOperationException()
        });
        Task<HttpResponseMessage> WaitForNav() { entered.TrySetResult(); return release.Task; }
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var challenge = await client.CreateQrLoginAsync(Token);
        var pending = client.PollQrLoginAsync(challenge.QrcodeKey, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        switch (change)
        {
            case 0: await client.CreateQrLoginAsync(Token); break;
            case 1: await client.SignOutAsync(Token); break;
            case 2: client.CancelQrLogin(); break;
            case 3: await client.ImportCookiesAsync("SESSDATA=new-token", Token); break;
        }
        release.SetResult(StubHttpHandler.Json(Account));

        Assert.Equal(QrLoginState.Superseded, (await pending).State);
        if (change == 3) Assert.Equal(84, client.CurrentAccount()!.Uid);
        else Assert.Null(client.CurrentAccount());
    }

    [Fact]
    public async Task LateCookieImportCannotReplaceANewerValidatedAccount()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler((request, _) =>
        {
            if (request.Headers.GetValues("Cookie").Contains("SESSDATA=old-token"))
            { entered.TrySetResult(); return release.Task; }
            return Task.FromResult(StubHttpHandler.Json(Account.Replace("42", "84", StringComparison.Ordinal)));
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var pending = client.ImportCookiesAsync("SESSDATA=old-token", Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await client.ImportCookiesAsync("SESSDATA=new-token", Token);
        release.SetResult(StubHttpHandler.Json(Account));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(84, client.CurrentAccount()!.Uid);
    }

    [Fact]
    public async Task LateQrGenerationCannotSupersedeTheNewerChallenge()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll", StringComparison.Ordinal))
                return Task.FromResult(StubHttpHandler.Json("""{"code":0,"data":{"code":86101}}"""));
            if (++calls == 1) { entered.TrySetResult(); return release.Task; }
            return Task.FromResult(Generate(2));
        });
        using var http = new HttpClient(handler);
        var client = new BilibiliClient(http);
        var oldRequest = client.CreateQrLoginAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var current = await client.CreateQrLoginAsync(Token);
        release.SetResult(Generate(1));
        var old = await oldRequest;

        Assert.Equal(QrLoginState.Superseded, (await client.PollQrLoginAsync(old.QrcodeKey, Token)).State);
        Assert.Equal(QrLoginState.WaitingScan, (await client.PollQrLoginAsync(current.QrcodeKey, Token)).State);
    }

    internal static HttpResponseMessage Generate(int version) => StubHttpHandler.Json(
        "{\"code\":0,\"data\":{\"url\":\"https://passport.bilibili.com/\",\"qrcode_key\":\"challenge-key-"
        + version.ToString("D16") + "\"}}");

    internal static HttpResponseMessage QrSuccess()
    {
        var result = StubHttpHandler.Json("""{"code":0,"data":{"code":0}}""");
        result.Headers.Add("Set-Cookie", "SESSDATA=qr-token; Path=/; Domain=.bilibili.com");
        return result;
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
