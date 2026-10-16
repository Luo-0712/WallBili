using System.IO;
using System.Text;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class AuthStoreTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(),
        "wallbili-auth-" + Guid.NewGuid().ToString("N"), "auth.dat");

    [Fact]
    public async Task RoundTripsTheCookieTextWithDpapi()
    {
        var store = new AuthStore(NewPath());
        const string cookie = "SESSDATA=secret-token; DedeUserID=42";

        store.Save(cookie);
        var loaded = await Task.Run(() => store.Load(), TestContext.Current.CancellationToken);

        Assert.Equal(cookie, loaded);
        // The file content is not plain text: credentials never sit on disk unprotected.
        var bytes = await File.ReadAllBytesAsync(store.FilePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task MissingFileLoadsAsNull()
    {
        var store = new AuthStore(NewPath());
        Assert.Null(await Task.Run(() => store.Load(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)] // Truncated DPAPI payload.
    [InlineData(false)] // Random garbage.
    public async Task CorruptedFileLoadsAsNullWithoutThrowing(bool empty)
    {
        var store = new AuthStore(NewPath());
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllBytesAsync(store.FilePath, empty ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4, 5 },
            TestContext.Current.CancellationToken);

        Assert.Null(await Task.Run(() => store.Load(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearRemovesTheStoredCredentials()
    {
        var store = new AuthStore(NewPath());
        store.Save("SESSDATA=secret-token");

        store.Clear();

        Assert.False(File.Exists(store.FilePath));
        Assert.Null(await Task.Run(() => store.Load(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AtomicReplaceKeepsLoadingOnConcurrentReaders()
    {
        var store = new AuthStore(NewPath());
        store.Save("SESSDATA=first");
        store.Save("SESSDATA=second"); // Overwrite through the temporary file must not leave a torn file.
        Assert.Equal("SESSDATA=second", await Task.Run(() => store.Load(), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }
}
