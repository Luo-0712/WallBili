using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WallBili.Core;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class McpSettingsTests
{
    [Fact]
    public void ExistingPreferencesKeepMcpDisabledOnTheDefaultPort()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "preferences.json");
        File.WriteAllText(path, "{\"MpvPath\":\"custom-mpv\",\"WallpaperEnabled\":false}");

        var preferences = DesktopPreferences.Read(path);

        Assert.False(preferences.McpEnabled);
        Assert.Equal(38291, preferences.McpPort);
        Assert.Equal("custom-mpv", preferences.MpvPath);
        Assert.False(preferences.WallpaperEnabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidStoredPortsRecoverToTheDefault(int port)
    {
        var path = Path.Combine(NewDirectory(), "preferences.json");
        File.WriteAllText(path, $$"""{"McpEnabled":true,"McpPort":{{port}}}""");

        Assert.Equal(38291, DesktopPreferences.Read(path).McpPort);
    }

    [Fact]
    public void McpPreferencesRoundTripWithoutStoringTheAccessToken()
    {
        var path = Path.Combine(NewDirectory(), "preferences.json");
        var preferences = new DesktopPreferences { McpEnabled = true, McpPort = 39123 };

        preferences.Save(path);

        Assert.Equal(preferences, DesktopPreferences.Read(path));
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TokenIsEncryptedStableAndIndependentFromTheBilibiliAccount()
    {
        var directory = NewDirectory();
        var account = new AuthStore(Path.Combine(directory, "auth.dat"));
        account.Save("SESSDATA=account-secret");
        var path = Path.Combine(directory, "mcp-token.dat");
        var store = new McpTokenStore(path);

        var token = store.GetOrCreate();

        Assert.True(token.Length >= 43);
        Assert.Equal(token, new McpTokenStore(path).GetOrCreate());
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.NotEqual(token, store.Reset());
        Assert.Equal("SESSDATA=account-secret", account.Load());
    }

    [Fact]
    public void CopiedConfigurationsShareAnEndpointAndKeepTokenOutOfBridgeArguments()
    {
        const string token = "secret-with-quote-\"-and-slash-\\";
        using var http = JsonDocument.Parse(McpConnectionConfiguration.Http(39123, token));
        using var stdio = JsonDocument.Parse(McpConnectionConfiguration.Stdio(39123, token, @"C:\Program Files\WallBili\mcp-bridge\WallBili.McpBridge.exe"));
        var remote = http.RootElement.GetProperty("mcpServers").GetProperty("wallbili");
        var bridge = stdio.RootElement.GetProperty("mcpServers").GetProperty("wallbili");

        Assert.Equal("http://127.0.0.1:39123/mcp", remote.GetProperty("url").GetString());
        Assert.Equal("Bearer " + token, remote.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("--endpoint", bridge.GetProperty("args")[0].GetString());
        Assert.Equal(remote.GetProperty("url").GetString(), bridge.GetProperty("args")[1].GetString());
        Assert.DoesNotContain(token, bridge.GetProperty("args").GetRawText());
        Assert.Equal(token, bridge.GetProperty("env").GetProperty("WALLBILI_MCP_TOKEN").GetString());
    }

    [Fact]
    public Task SettingsDialogValidatesPortBeforeChangingTheLiveService() => OnSta(() =>
    {
        (bool Enabled, int Port)? applied = null;
        var dialog = new McpSettingsDialog(new(), () => "未启用", (enabled, port) =>
        { applied = (enabled, port); return Task.CompletedTask; }, () => Task.CompletedTask, _ => "{}");
        var controls = Descendants((FrameworkElement)dialog.Content).ToArray();
        var portInput = controls.OfType<TextBox>().Single();
        var enabledBox = controls.OfType<CheckBox>().Single();
        var apply = controls.OfType<Button>().Single(b => Equals(b.Content, "应用连接设置"));
        portInput.Text = "70000";

        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(applied);
        Assert.Contains(controls.OfType<TextBlock>(), text => text.Text.Contains("1–65535"));

        portInput.Text = "39001"; enabledBox.IsChecked = true;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((true, 39001), applied);
        dialog.Close();
    });

    [Fact]
    public Task PlaylistDialogShowsPreparationAndUsesVersionedApplicationCommands() => OnSta(() =>
    {
        var id = Guid.NewGuid(); var taskId = Guid.NewGuid();
        PlaylistMember[] members = [
            new(Guid.NewGuid(), new("video", "BV1GJ411x7h7"), "第一课", [new("BV1GJ411x7h7", 1), new("BV1GJ411x7h7", 2)]),
            new(Guid.NewGuid(), new("video", "BV1xx411c7mD"), "第二课", [new("BV1xx411c7mD", 3)])];
        var playlist = new LocalPlaylist(id, "摄影教程", 7, [], members, [], taskId, DateTimeOffset.UtcNow);
        var snapshot = new LibrarySnapshot([], [], [], new(), "", Agent: new([playlist],
            [new(taskId, id, [], "running", 2, 100)], 11));
        var calls = new List<(string Tool, JsonElement Arguments)>();
        var dialog = new LocalPlaylistsDialog(() => snapshot, (tool, args) =>
        {
            calls.Add((tool, JsonSerializer.SerializeToElement(args, AgentJson.Options)));
            return Task.FromResult(AgentResult.Success("已接受", new { }));
        });
        var controls = Descendants((FrameworkElement)dialog.Content).ToArray();
        Assert.Contains(controls.OfType<TextBlock>(), t => t.Text.Contains("2 / 100"));
        var list = controls.OfType<ListBox>().Single(l => l.Name == "PlaylistMembersList");
        list.SelectedIndex = 1;
        controls.OfType<Button>().Single(b => Equals(b.Content, "上移")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("edit_playlist", calls[0].Tool);
        Assert.Equal("reorder", calls[0].Arguments.GetProperty("action").GetString());
        Assert.Equal(7, calls[0].Arguments.GetProperty("expectedRevision").GetInt64());
        Assert.Equal(members[1].Id, calls[0].Arguments.GetProperty("memberIds")[0].GetGuid());

        controls.OfType<Button>().Single(b => Equals(b.Content, "替换队列并播放")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("load_playlist", calls[1].Tool);
        Assert.Equal(2, calls[1].Arguments.GetProperty("startIndex").GetInt32());
        Assert.Equal(11, calls[1].Arguments.GetProperty("expectedRevision").GetInt64());
        Assert.True(calls[1].Arguments.GetProperty("autoplay").GetBoolean());
        controls.OfType<Button>().Single(b => Equals(b.Content, "加入队列")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("append", calls[2].Arguments.GetProperty("mode").GetString());
        Assert.False(calls[2].Arguments.GetProperty("autoplay").GetBoolean());
        dialog.Close();
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static IEnumerable<FrameworkElement> Descendants(FrameworkElement root)
    {
        yield return root;
        if (root is Panel panel)
            foreach (var child in panel.Children.OfType<FrameworkElement>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        else if (root is Decorator { Child: FrameworkElement child })
            foreach (var descendant in Descendants(child)) yield return descendant;
        else if (root is ContentControl { Content: FrameworkElement content })
            foreach (var descendant in Descendants(content)) yield return descendant;
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wallbili-mcp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
