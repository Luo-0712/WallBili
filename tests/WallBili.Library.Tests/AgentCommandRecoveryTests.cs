using System.Text.Json;
using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class AgentCommandRecoveryTests
{
    private const string Bvid = "BV1GJ411x7h7";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args, AgentJson.Options);
    private static TestContent Content()
    {
        var content = new TestContent();
        content.Videos[Bvid] = new(Bvid, "Course", "UP", 1, TimeSpan.FromSeconds(60),
            [new(1, new(Bvid, 1), "First", TimeSpan.FromSeconds(30)), new(2, new(Bvid, 2), "Second", TimeSpan.FromSeconds(30))]);
        return content;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task AcceptedCreationSurvivesHttpCancellationAndRetriesReturnSamePlaylist()
    {
        var store = new FaultStore(); var content = Content(); var player = new TestPlayer();
        await using var session = new LibrarySession(content, store, player);
        await session.InitializeAsync(Token);
        await using var service = new AgentControlService(session, content);
        using var disconnected = new CancellationTokenSource();
        store.OnSave = state => { if (state.Agent.Playlists.Count > 0) disconnected.Cancel(); };
        var args = Args(new { name = "Course", references = new[] { new ContentRef("video", Bvid) }, requestId = "accepted" });
        var first = await service.InvokeAsync("create_playlist", args, disconnected.Token);
        Assert.True(disconnected.IsCancellationRequested);
        Assert.False(first.IsError, first.Summary);
        var retry = await service.InvokeAsync("create_playlist", args, Token);
        Assert.Equal(first.Data.GetProperty("playlistId").GetGuid(), retry.Data.GetProperty("playlistId").GetGuid());
        Assert.Single(session.Snapshot.Agent!.Playlists);
        await Until(() => session.Snapshot.Agent!.Tasks.Single().State == "completed");
        store.OnSave = null;
    }

    [Fact]
    public async Task PersistenceFailureAfterCreationCannotDuplicateAndStillSchedulesAcceptedTask()
    {
        var store = new FaultStore(); var content = Content(); var player = new TestPlayer();
        await using var session = new LibrarySession(content, store, player);
        await session.InitializeAsync(Token);
        await using var service = new AgentControlService(session, content);
        store.FailOnceWhen = state => state.Agent.Playlists.Count > 0;
        var args = Args(new { name = "Course", references = new[] { new ContentRef("video", Bvid) }, requestId = "ambiguous" });
        var failed = await service.InvokeAsync("create_playlist", args, Token);
        Assert.True(failed.IsError);
        var retry = await service.InvokeAsync("create_playlist", args, Token);
        Assert.True(retry.IsError);
        Assert.Equal("Accepted", retry.Data.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(session.Snapshot.Agent!.Playlists);
        await Until(() => session.Snapshot.Agent!.Tasks.Single().State == "completed");
        Assert.Single((await store.LoadAsync(Token)).Agent.Playlists);
    }

    [Fact]
    public async Task FailedNextCheckpointNeverAdvancesTwiceForSameRequestId()
    {
        var store = new FaultStore(); var content = Content(); var player = new TestPlayer();
        await using var session = new LibrarySession(content, store, player);
        await session.InitializeAsync(Token); await session.ImportVideoAsync(Bvid, Token);
        await session.EnqueueAsync(session.Snapshot.Entries.Select(e => e.Key), Token);
        await session.SetModeAsync(QueueMode.RepeatAll, Token); await session.PlayAsync(token: Token);
        await using var service = new AgentControlService(session, content);
        var initial = session.Snapshot.Playback.CurrentId;
        store.FailOnceWhen = state => state.Playback.CurrentId != initial;
        var args = Args(new { action = "next", requestId = "next-once" });
        Assert.True((await service.InvokeAsync("control_playback", args, Token)).IsError);
        var changed = session.Snapshot.Playback.CurrentId;
        Assert.NotEqual(initial, changed);
        var count = player.Loads.Count;
        Assert.True((await service.InvokeAsync("control_playback", args, Token)).IsError);
        Assert.Equal(changed, session.Snapshot.Playback.CurrentId);
        Assert.Equal(count, player.Loads.Count);
    }

    [Theory]
    [InlineData("{\"action\":\"replace\",\"expectedRevision\":2}")]
    [InlineData("{\"action\":\"clear\",\"expectedRevision\":\"2\"}")]
    [InlineData("{\"action\":\"replace\",\"references\":[{\"kind\":\"part\",\"bvid\":\"BV1GJ411x7h7\",\"cid\":1,\"url\":\"https://invalid/\"}],\"expectedRevision\":2}")]
    public async Task InvalidQueueEditsDoNotMutateAnything(string json)
    {
        var store = new FaultStore(); var content = Content(); var player = new TestPlayer();
        await using var session = new LibrarySession(content, store, player);
        await session.InitializeAsync(Token); await session.ImportVideoAsync(Bvid, Token);
        await session.EnqueueAsync(session.Snapshot.Entries.Select(e => e.Key), Token);
        var queue = session.Snapshot.Queue.ToArray();
        await using var service = new AgentControlService(session, content);
        var result = await service.InvokeAsync("edit_queue", JsonDocument.Parse(json).RootElement, Token);
        Assert.True(result.IsError);
        Assert.Equal("InvalidInput", result.Data.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(queue, session.Snapshot.Queue);
    }

    [Fact]
    public async Task AccountChangeWhileCommitWaitsForLibraryGateRejectsStaleMetadata()
    {
        var store = new FaultStore(); var player = new TestPlayer(); var versioned = new SessionContent(Content());
        await using var session = new LibrarySession(versioned, store, player);
        await session.InitializeAsync(Token);
        await using var service = new AgentControlService(session, versioned);
        var created = await service.InvokeAsync("create_playlist", Args(new { name = "Course", references = new[] { new ContentRef("video", Bvid) } }), Token);
        Assert.False(created.IsError, created.Summary);
        await Until(() => Volatile.Read(ref versioned.Reads) >= 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSaveAsync = async (state, ct) => { if (state.Playback.Volume == 55) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var holder = session.SetVolumeAsync(55, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            versioned.Metadata.SetResult();
            await Until(() => Volatile.Read(ref versioned.Reads) >= 3);
            Volatile.Write(ref versioned.Version, 1);
        }
        finally { store.BeforeSaveAsync = null; release.TrySetResult(); }
        await holder;
        await Until(() => session.Snapshot.Agent!.Tasks.Single().State == "failed");
        Assert.Equal("SessionChanged", session.Snapshot.Agent!.Tasks.Single().Failure);
        Assert.Empty(session.Snapshot.Entries);
        Assert.Empty(session.Snapshot.Agent.Playlists.Single().Members);
    }

    [Fact]
    public async Task RemovingSourceDuringManualRefreshRejectsLateImportButAllowsExplicitReimport()
    {
        var store = new FaultStore(); var player = new TestPlayer(); var content = Content();
        await using var session = new LibrarySession(content, store, player);
        await session.InitializeAsync(Token); await session.ImportVideoAsync(Bvid, Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.CustomGetVideo = async (input, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return content.Videos[input]; };
        var refresh = session.ImportVideoAsync(Bvid, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await session.RemoveSourceAsync("video:" + Bvid, Token);
        release.SetResult();
        await Assert.ThrowsAsync<AgentControlException>(() => refresh);
        Assert.Empty(session.Snapshot.Sources);
        content.CustomGetVideo = null;
        await session.ImportVideoAsync(Bvid, Token);
        Assert.Single(session.Snapshot.Sources);
    }

    private sealed class SessionContent(IContentClient inner) : IContentClient, IContentSessionVersion
    {
        public int Version, Reads;
        public int SessionVersion { get { Interlocked.Increment(ref Reads); return Volatile.Read(ref Version); } }
        public TaskCompletionSource Metadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default)
        { await Metadata.Task.WaitAsync(cancellationToken); return await inner.GetVideoAsync(input, cancellationToken); }
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default) => inner.ResolveMediaAsync(key, cancellationToken);
        public Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            inner.GetCollectionPageAsync(source, page, pageSize, cancellationToken);
    }

    private sealed class FaultStore : ILibraryStore
    {
        private string _json = JsonSerializer.Serialize(new LibraryState());
        public Action<LibraryState>? OnSave;
        public Func<LibraryState, CancellationToken, Task>? BeforeSaveAsync;
        public Func<LibraryState, bool>? FailOnceWhen;
        public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(JsonSerializer.Deserialize<LibraryState>(_json)!);
        public async Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
        {
            if (BeforeSaveAsync is { } before) await before(state, cancellationToken);
            OnSave?.Invoke(state);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOnceWhen?.Invoke(state) == true) { FailOnceWhen = null; throw new IOException("Simulated interrupted disk commit."); }
            _json = JsonSerializer.Serialize(state);
        }
    }
}
