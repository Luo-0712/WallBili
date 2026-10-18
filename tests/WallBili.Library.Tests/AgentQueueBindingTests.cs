using System.Text.Json;
using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class AgentQueueBindingTests
{
    private const string A = "BV1GJ411x7h7", B = "BV17x411w7KC", C = "BV1xs411Q7xF";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppendRetainsEarlierPreparationAndWaitsAtItsBoundaryBeforeLaterReadyPlaylist()
    {
        await using var f = await FixtureAsync();
        var release = Block(f, B);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("collection", UploaderId: 1, CollectionId: 10));
        var second = await CreateAsync(service, "Second", new("video", C));
        await Until(() => f.Session.Snapshot.Agent!.Playlists.All(p => p.Members.Length > 0));
        await f.Session.LoadPlaylistAsync(first, autoplay: true, token: Token);
        await f.Session.LoadPlaylistAsync(second, token: Token);
        Assert.Equal(new[] { A, C }, Keys(f));
        f.Player.End();
        await Until(() => f.Session.Snapshot.Agent!.WaitingForContent || f.Player.Loads.Count > 1);
        Assert.True(f.Session.Snapshot.Agent!.WaitingForContent);
        Assert.Single(f.Player.Loads);
        release.SetResult();
        await Until(() => f.Player.Loads.Count == 2 && Keys(f).SequenceEqual([A, B, C]));
        Assert.Equal(new[] { A, B, C }, Keys(f));
        Assert.Equal(B, f.Player.Loads[^1].Key.Bvid);
        f.Player.End();
        await Until(() => f.Player.Loads.Count == 3);
        Assert.Equal(C, f.Player.Loads[^1].Key.Bvid);
    }

    [Fact]
    public async Task EmptyEarlierBatchArrivesBeforeReadyLaterBatchAndDoesNotPlayWithoutAutoplay()
    {
        await using var f = await FixtureAsync();
        var release = Block(f, A);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("video", A));
        var second = await CreateAsync(service, "Second", new("video", C));
        await f.Session.LoadPlaylistAsync(first, token: Token);
        await f.Session.LoadPlaylistAsync(second, token: Token);
        await Until(() => Keys(f).SequenceEqual([C]));
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.Equal(new[] { A, C }, Keys(f));
        Assert.Empty(f.Player.Loads);
        await f.Session.PlayAsync(token: Token);
        Assert.Equal(A, f.Player.Loads[^1].Key.Bvid);
    }

    [Fact]
    public async Task NaturalPlaybackIntoLaterBatchAlsoWaitsForItsUnpreparedTail()
    {
        await using var f = await FixtureAsync();
        f.Content.Collections[20] = [C, A];
        var release = Block(f, A);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("video", B));
        var second = await CreateAsync(service, "Second", new("collection", UploaderId: 1, CollectionId: 20));
        await Until(() => f.Session.Snapshot.Agent!.Playlists.All(p => p.Members.Length > 0));
        await f.Session.LoadPlaylistAsync(first, autoplay: true, token: Token);
        await f.Session.LoadPlaylistAsync(second, token: Token);
        f.Player.End();
        await Until(() => f.Player.Loads.Count == 2 && f.Session.Snapshot.Current?.Key.Bvid == C);
        f.Player.End();
        await Until(() => f.Session.Snapshot.Agent!.WaitingForContent);
        Assert.Equal(2, f.Player.Loads.Count);
        release.SetResult();
        await Until(() => f.Player.Loads.Count == 3 && Keys(f).SequenceEqual([B, C, A]));
        Assert.Equal(A, f.Player.Loads[^1].Key.Bvid);
    }

    [Fact]
    public async Task ExplicitAutoplayOfNewPlaylistDetachesOldPreparationWithoutRemovingExistingQueue()
    {
        await using var f = await FixtureAsync();
        var release = Block(f, B);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("collection", UploaderId: 1, CollectionId: 10));
        var second = await CreateAsync(service, "Second", new("video", C));
        await Until(() => f.Session.Snapshot.Agent!.Playlists.All(p => p.Members.Length > 0));
        await f.Session.LoadPlaylistAsync(first, autoplay: true, token: Token);
        await f.Session.LoadPlaylistAsync(second, autoplay: true, token: Token);
        Assert.Equal(C, f.Player.Loads[^1].Key.Bvid);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.Equal(new[] { A, C }, Keys(f));
    }

    [Fact]
    public async Task PauseBeforeFirstMetadataPreservesPreparedStartUntilResume()
    {
        await using var f = await FixtureAsync();
        var release = Block(f, A);
        await using var service = new AgentControlService(f.Session, f.Content);
        var id = await CreateAsync(service, "First", new("video", A));
        await f.Session.LoadPlaylistAsync(id, autoplay: true, token: Token);
        await f.Session.PauseAsync(Token);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.Empty(f.Player.Loads);
        Assert.Equal(new[] { A }, Keys(f));
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        await f.Session.PlayAsync(token: Token);
        Assert.Single(f.Player.Loads);
        Assert.Equal(A, f.Player.Loads[^1].Key.Bvid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrStopWhileWaitingStillFillsAllBatchesWithoutLateAutoplay(bool stop)
    {
        await using var f = await FixtureAsync();
        var release = Block(f, B);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("collection", UploaderId: 1, CollectionId: 10));
        var second = await CreateAsync(service, "Second", new("video", C));
        await Until(() => f.Session.Snapshot.Agent!.Playlists.All(p => p.Members.Length > 0));
        await f.Session.LoadPlaylistAsync(first, autoplay: true, token: Token);
        await f.Session.LoadPlaylistAsync(second, token: Token);
        f.Player.End();
        await Until(() => f.Session.Snapshot.Agent!.WaitingForContent || f.Player.Loads.Count > 1);
        Assert.Single(f.Player.Loads);
        if (stop) await f.Session.StopAsync(Token); else await f.Session.PauseAsync(Token);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.Equal(new[] { A, B, C }, Keys(f));
        Assert.Single(f.Player.Loads);
        Assert.Equal(stop ? PlaybackIntent.Stopped : PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        if (!stop)
        {
            await f.Session.PlayAsync(token: Token);
            Assert.Equal(B, f.Player.Loads[^1].Key.Bvid);
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("next")]
    [InlineData("play")]
    public async Task ExplicitQueueReplacementOrPlaybackSelectionDetachesEveryOldFeed(string action)
    {
        await using var f = await FixtureAsync();
        var release = Block(f, B);
        await using var service = new AgentControlService(f.Session, f.Content);
        var first = await CreateAsync(service, "First", new("collection", UploaderId: 1, CollectionId: 10));
        var second = await CreateAsync(service, "Second", new("video", C));
        await Until(() => f.Session.Snapshot.Agent!.Playlists.All(p => p.Members.Length > 0));
        await f.Session.LoadPlaylistAsync(first, autoplay: true, token: Token);
        await f.Session.LoadPlaylistAsync(second, token: Token);
        if (action == "clear") await f.Session.EditAgentQueueAsync("clear", expectedRevision: f.Session.Snapshot.Agent!.QueueRevision, token: Token);
        else if (action == "next") await f.Session.NextAsync(Token);
        else await f.Session.PlayAsync(f.Session.Snapshot.Queue[^1].Id, Token);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.DoesNotContain(B, Keys(f));
        if (action == "clear") Assert.Empty(f.Session.Snapshot.Queue);
        else Assert.Equal(C, f.Player.Loads[^1].Key.Bvid);
    }

    [Fact]
    public async Task RemovingSourceWhileBackgroundRefreshRunsPreventsResurrection()
    {
        await using var f = await FixtureAsync();
        var release = Block(f, B);
        await using var service = new AgentControlService(f.Session, f.Content);
        await CreateAsync(service, "First", new("collection", UploaderId: 1, CollectionId: 10));
        await Until(() => f.Session.Snapshot.Sources.Count > 0);
        var sourceId = f.Session.Snapshot.Sources[0].Id;
        await f.Session.RemoveSourceAsync(sourceId, Token);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.DoesNotContain(f.Session.Snapshot.Sources, source => source.Id == sourceId);
        Assert.Contains(f.Session.Snapshot.Entries, entry => entry.Key.Bvid == B);
    }

    private static async Task<LibraryFixture> FixtureAsync()
    {
        var f = new LibraryFixture();
        foreach (var (old, bvid) in new[] { (TestContent.A, A), (TestContent.B, B), (TestContent.C, C) })
        {
            var video = f.Content.Videos[old];
            f.Content.Videos[bvid] = video with { Bvid = bvid, Parts = [video.Parts[0] with { Key = new(bvid, video.Parts[0].Key.Cid) }] };
        }
        f.Content.Collections[10] = [A, B];
        await f.Session.InitializeAsync(Token);
        return f;
    }
    private static TaskCompletionSource Block(LibraryFixture f, string bvid)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Content.CustomGetVideo = async (input, ct) =>
        {
            if (input == bvid) await release.Task.WaitAsync(ct);
            return f.Content.Videos[input];
        };
        return release;
    }
    private static async Task<Guid> CreateAsync(AgentControlService service, string name, ContentRef reference)
    {
        var result = await service.InvokeAsync("create_playlist", JsonSerializer.SerializeToElement(new { name, references = new[] { reference } }, AgentJson.Options), Token);
        Assert.False(result.IsError, result.Summary);
        return result.Data.GetProperty("playlistId").GetGuid();
    }
    private static string[] Keys(LibraryFixture f) => f.Session.Snapshot.Queue.Select(q => q.Key.Bvid).ToArray();
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
