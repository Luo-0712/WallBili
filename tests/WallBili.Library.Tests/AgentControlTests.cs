using System.Text.Json;
using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class AgentControlTests
{
    private const string A = "BV1GJ411x7h7", B = "BV17x411w7KC";
    private static LibraryFixture NewFixture()
    {
        var f = new LibraryFixture();
        foreach (var (old, id) in new[] { (TestContent.A, A), (TestContent.B, B) })
        {
            var video = f.Content.Videos[old];
            f.Content.Videos[id] = video with { Bvid = id, Parts = video.Parts.Select(p => p with { Key = new(id, p.Key.Cid) }).ToArray() };
        }
        f.Content.Collections[10] = [A, B];
        return f;
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<AgentResult> Call(AgentControlService service, string tool, object args) =>
        service.InvokeAsync(tool, JsonSerializer.SerializeToElement(args, AgentJson.Options), Token);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task StartsFirstVideoBeforeRemainingMetadataAndWaitsWithoutLooping()
    {
        await using var f = NewFixture();
        await f.Session.InitializeAsync(Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Content.CustomGetVideo = async (input, ct) =>
        {
            if (input == B) await release.Task.WaitAsync(ct);
            return f.Content.Videos[input];
        };
        await using var service = new AgentControlService(f.Session, f.Content);
        var created = await Call(service, "create_playlist", new { name = "Course", references = new[] { new ContentRef("collection", UploaderId: 1, CollectionId: 10, CollectionKind: CollectionKind.Season) }, requestId = "create" });
        Assert.False(created.IsError, created.Summary);
        var playlist = created.Data.GetProperty("playlistId").GetGuid();
        var load = await Call(service, "load_playlist", new { playlistId = playlist, autoplay = true, requestId = "load" });
        Assert.False(load.IsError, load.Summary);
        await Until(() => f.Player.Loads.Count == 1);
        Assert.Equal(A, f.Player.Loads[0].Key.Bvid);
        Assert.Equal(2, f.Session.Snapshot.Queue.Count);
        await f.Session.SetModeAsync(QueueMode.RepeatAll, Token);
        f.Player.End();
        await Until(() => f.Player.Loads.Count == 2);
        f.Player.End();
        await Until(() => f.Session.Snapshot.Agent!.WaitingForContent);
        Assert.Equal(2, f.Player.Loads.Count);
        release.SetResult();
        await Until(() => f.Player.Loads.Count == 3);
        Assert.Equal(B, f.Player.Loads[^1].Key.Bvid);
    }

    [Fact]
    public async Task StopPreventsLateMetadataFromResumingAndRequestIdsPreventDuplicatePlaylist()
    {
        await using var f = NewFixture();
        await f.Session.InitializeAsync(Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Content.CustomGetVideo = async (input, ct) => { await release.Task.WaitAsync(ct); return f.Content.Videos[input]; };
        await using var service = new AgentControlService(f.Session, f.Content);
        var args = new { name = "Course", references = new[] { new ContentRef("video", A) }, requestId = "same" };
        var first = await Call(service, "create_playlist", args);
        var duplicate = await Call(service, "create_playlist", args);
        Assert.False(first.IsError, first.Summary);
        Assert.Equal(first.Data.GetProperty("playlistId").GetGuid(), duplicate.Data.GetProperty("playlistId").GetGuid());
        Assert.Single(f.Session.Snapshot.Agent!.Playlists);
        await Call(service, "load_playlist", new { playlistId = first.Data.GetProperty("playlistId").GetGuid(), autoplay = true });
        await f.Session.StopAsync(Token);
        release.SetResult();
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        Assert.Empty(f.Player.Loads);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
        var reused = await Call(service, "create_playlist", new { name = "Different", references = args.references, requestId = "same" });
        Assert.True(reused.IsError);
        Assert.Equal("Conflict", reused.Data.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RejectsStaleQueueEditAndKeepsQueuedContentWhenPlaylistDeleted()
    {
        await using var f = NewFixture(); await f.SeedAsync();
        await using var service = new AgentControlService(f.Session, f.Content);
        var revision = f.Session.Snapshot.Agent!.QueueRevision;
        await f.Session.EnqueueAsync([new(A, 11)], Token);
        var stale = await Call(service, "edit_queue", new { action = "clear", expectedRevision = revision });
        Assert.True(stale.IsError);
        Assert.Equal(5, f.Session.Snapshot.Queue.Count);
        var created = await Call(service, "create_playlist", new { name = "Saved", references = new[] { new ContentRef("video", A) } });
        Assert.False(created.IsError, created.Summary);
        await Until(() => f.Session.Snapshot.Agent!.Tasks.All(t => t.State == "completed"));
        var queue = f.Session.Snapshot.Queue.ToArray();
        var saved = f.Session.Snapshot.Agent!.Playlists.Single();
        var deleted = await Call(service, "delete_playlist", new { playlistId = saved.Id, expectedRevision = saved.Revision });
        Assert.False(deleted.IsError, deleted.Summary);
        Assert.Equal(queue, f.Session.Snapshot.Queue);
        Assert.Empty((await f.Store.LoadAsync(Token)).Agent.Playlists);
    }

    [Fact]
    public async Task NextWithSameRequestIdOnlyAdvancesOnceAndDoesNotExposeMediaSecrets()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync(); await f.Session.PlayAsync(token: Token);
        await using var service = new AgentControlService(f.Session, f.Content);
        var args = new { action = "next", requestId = "once" };
        await Call(service, "control_playback", args);
        var id = f.Session.Snapshot.Playback.CurrentId;
        await Call(service, "control_playback", args);
        Assert.Equal(id, f.Session.Snapshot.Playback.CurrentId);
        var status = await Call(service, "get_status", new { });
        Assert.True(status.Data.GetProperty("outputActive").GetBoolean());
        await f.Session.PauseAsync(Token);
        var paused = await Call(service, "get_status", new { });
        Assert.False(paused.Data.GetProperty("outputActive").GetBoolean());
        Assert.DoesNotContain("temporary-secret", status.Data.GetRawText());
        Assert.DoesNotContain("Headers", status.Data.GetRawText());
    }
}
