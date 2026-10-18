using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WallBili.Core;
using WallBili.Mcp;
using Xunit;

namespace WallBili.Mcp.Tests;

/// <summary>Business workflows cross the official SDK, HTTP transport, application command layer,
/// library, preparation jobs and player boundary. No real account, media service or user data is used.</summary>
public sealed class McpWorkflowTests
{
    private const string A = "BV1GJ411x7h7", B = "BV17x411w7KC";
    private const string AccessToken = "workflow-only-mcp-access-token-0123456789";
    private const string MediaSecret = "workflow-signed-media-secret";
    private const string CookieSecret = "workflow-cookie-secret";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AssistantCanDiscoverPlayEditAndDeleteAPlaylistThroughHttpWithoutReceivingMediaCredentials()
    {
        await using var runtime = await Runtime.StartAsync(new WorkflowContent([A, B]));
        await using var assistant = await ConnectAsync(runtime.Server.Endpoint);
        var discovered = await Success(assistant, "search_content", new { query = "摄影入门", pageSize = 10 });
        var firstReference = discovered.GetProperty("items")[0].GetProperty("reference").Clone();
        var secondReference = discovered.GetProperty("items")[1].GetProperty("reference").Clone();
        Assert.Equal(A, firstReference.GetProperty("bvid").GetString());
        Assert.Empty(runtime.Content.Resolved);
        var detail = await Success(assistant, "get_content", new { reference = firstReference });
        Assert.Equal(new[] { 11L, 12L }, detail.GetProperty("parts").EnumerateArray()
            .Select(p => p.GetProperty("reference").GetProperty("cid").GetInt64()).ToArray());
        Assert.Empty(runtime.Content.Resolved);

        var createArguments = new { name = "摄影入门", references = new[] { firstReference, secondReference }, requestId = "create-course" };
        var created = await Success(assistant, "create_playlist", createArguments);
        var duplicate = await Success(assistant, "create_playlist", createArguments);
        var playlistId = created.GetProperty("playlistId").GetGuid();
        Assert.Equal(playlistId, duplicate.GetProperty("playlistId").GetGuid());
        Assert.Single(runtime.Library.Snapshot.Agent!.Playlists);
        await Until(() => runtime.Library.Snapshot.Agent!.Tasks.Single().State == "completed");
        await Success(assistant, "load_playlist", new { playlistId, autoplay = true, requestId = "play-course" });
        var status = await Success(assistant, "get_status", new { });
        Assert.True(status.GetProperty("outputActive").GetBoolean());
        Assert.Equal("playing", status.GetProperty("playback").GetProperty("intent").GetString());
        Assert.Equal(11, status.GetProperty("current").GetProperty("key").GetProperty("cid").GetInt64());
        Assert.Single(runtime.Player.Loads);

        await Success(assistant, "control_playback", new { action = "pause" });
        await Success(assistant, "control_playback", new { action = "seek", seconds = 25 });
        status = await Success(assistant, "get_status", new { });
        Assert.Equal("paused", status.GetProperty("playback").GetProperty("intent").GetString());
        Assert.Equal(25, status.GetProperty("playback").GetProperty("positionSeconds").GetDouble());
        Assert.True(runtime.Player.Paused);
        Assert.Equal(TimeSpan.FromSeconds(25), runtime.Player.Position);

        var queue = await Success(assistant, "get_queue", new { });
        var revision = queue.GetProperty("revision").GetInt64();
        var queueItems = queue.GetProperty("queue").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 11L, 12L, 21L }, queueItems.Select(q => q.GetProperty("reference").GetProperty("cid").GetInt64()).ToArray());
        var removeArguments = new { action = "remove", itemIds = new[] { queueItems[1].GetProperty("id").GetGuid() },
            expectedRevision = revision, requestId = "remove-second-part" };
        await Success(assistant, "edit_queue", removeArguments);
        await Success(assistant, "edit_queue", removeArguments);
        var edited = await Success(assistant, "get_queue", new { });
        Assert.Equal(2, edited.GetProperty("queue").GetProperty("total").GetInt32());
        Assert.True(edited.GetProperty("revision").GetInt64() > revision);
        var stale = await Invoke(assistant, "edit_queue", new { action = "remove", itemIds = new[] { queueItems[2].GetProperty("id").GetGuid() },
            expectedRevision = revision, requestId = "stale-remove" });
        Assert.True(stale.IsError);
        Assert.Equal("Conflict", Data(stale).GetProperty("error").GetProperty("code").GetString());

        var nextArguments = new { action = "next", requestId = "next-once" };
        await Success(assistant, "control_playback", nextArguments);
        var loads = runtime.Player.Loads.Count;
        await Success(assistant, "control_playback", nextArguments);
        Assert.Equal(loads, runtime.Player.Loads.Count);
        Assert.Equal(B, runtime.Library.Snapshot.Current!.Key.Bvid);

        var playlist = await Success(assistant, "get_playlist", new { playlistId });
        var preservedIds = runtime.Library.Snapshot.Queue.Select(q => q.Id).ToArray();
        await Success(assistant, "delete_playlist", new { playlistId,
            expectedRevision = playlist.GetProperty("revision").GetInt64(), requestId = "delete-course" });
        Assert.Empty(runtime.Library.Snapshot.Agent!.Playlists);
        Assert.Equal(preservedIds, runtime.Library.Snapshot.Queue.Select(q => q.Id).ToArray());
        Assert.True((await Success(assistant, "get_status", new { })).GetProperty("outputActive").GetBoolean());
        var malformed = await Invoke(assistant, "get_content", new { reference = new { kind = "part", bvid = A, cid = 0 } });
        Assert.True(malformed.IsError);
        Assert.Equal("InvalidInput", Data(malformed).GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(MediaSecret, JsonSerializer.Serialize(runtime.Store.Saved, AgentJson.Options));
        Assert.DoesNotContain(CookieSecret, JsonSerializer.Serialize(runtime.Store.Saved, AgentJson.Options));
    }

    [Fact]
    public async Task LongCollectionStartsFirstPartsThenContinuesPreparationAndPlaybackAcrossAssistantDisconnect()
    {
        var videoIds = new[] { A, B }.Concat(Enumerable.Range(1, 99).Select(i => "BV" + i.ToString("D10"))).ToArray();
        var content = new WorkflowContent(videoIds) { BlockVideo = B };
        await using var runtime = await Runtime.StartAsync(content);
        Guid taskId, playlistId;
        try
        {
            await using (var assistant = await ConnectAsync(runtime.Server.Endpoint))
            {
                var created = await Success(assistant, "create_playlist", new { name = "101 节系列课",
                    references = new[] { new ContentRef("collection", UploaderId: 42, CollectionId: 100, CollectionKind: CollectionKind.Season) } });
                taskId = created.GetProperty("taskId").GetGuid();
                playlistId = created.GetProperty("playlistId").GetGuid();
                await Success(assistant, "load_playlist", new { playlistId, autoplay = true });
                await content.BlockedMetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
                var status = await Success(assistant, "get_status", new { });
                Assert.True(status.GetProperty("outputActive").GetBoolean());
                Assert.Equal(A, status.GetProperty("current").GetProperty("key").GetProperty("bvid").GetString());
                Assert.False(content.ReleaseMetadata.Task.IsCompleted);
                Assert.Equal(1, content.CollectionPageCalls);
                Assert.Equal(new[] { 11L, 12L }, runtime.Library.Snapshot.Queue.Select(q => q.Key.Cid).ToArray());
                Assert.Equal("running", (await Success(assistant, "get_task", new { taskId })).GetProperty("state").GetString());
                await Success(assistant, "control_playback", new { action = "set_mode", mode = "repeatAll" });
                runtime.Player.FinishCurrent();
                await Until(() => runtime.Player.Loads.Count == 2 && runtime.Library.Snapshot.Current?.Key.Cid == 12);
                runtime.Player.FinishCurrent();
                await Until(() => runtime.Library.Snapshot.Agent!.WaitingForContent);
                status = await Success(assistant, "get_status", new { });
                Assert.True(status.GetProperty("waitingForContent").GetBoolean());
                Assert.False(status.GetProperty("outputActive").GetBoolean());
                Assert.Equal(2, runtime.Player.Loads.Count); // Repeat mode must not loop through the prepared prefix.
            }

            // No client remains connected when the delayed metadata is released. These jobs belong
            // to the application, so disposing the MCP client must not propagate cancellation here.
            Assert.False(content.BlockedMetadataCancelled);
            content.ReleaseMetadata.TrySetResult();
            await Until(() => runtime.Library.Snapshot.Agent!.Tasks.Single(t => t.Id == taskId).State == "completed");
            Assert.False(content.BlockedMetadataCancelled);
            Assert.Equal(4, content.CollectionPageCalls);
            Assert.Equal(102, runtime.Library.Snapshot.Queue.Count);
            Assert.Equal(B, runtime.Library.Snapshot.Current!.Key.Bvid);
            Assert.Equal(3, runtime.Player.Loads.Count);

            await using var reconnected = await ConnectAsync(runtime.Server.Endpoint);
            var recovered = await Success(reconnected, "get_status", new { });
            Assert.True(recovered.GetProperty("outputActive").GetBoolean());
            Assert.False(recovered.GetProperty("waitingForContent").GetBoolean());
            Assert.Equal("completed", (await Success(reconnected, "get_task", new { taskId })).GetProperty("state").GetString());
            var queue = await Success(reconnected, "get_queue", new { pageSize = 100 });
            Assert.Equal(102, queue.GetProperty("queue").GetProperty("total").GetInt32());
            Assert.True(queue.GetProperty("queue").GetProperty("hasMore").GetBoolean());
            var expected = videoIds.SelectMany(id => content.Videos[id].Parts.OrderBy(p => p.Page)).Select(p => p.Key).ToArray();
            Assert.Equal(expected, runtime.Library.Snapshot.Queue.Select(q => q.Key).ToArray());
        }
        finally { content.ReleaseMetadata.TrySetResult(); }
    }

    [Fact]
    public async Task PausingBeforeTheFirstMetadataArrivesKeepsPlaybackPausedUntilAssistantExplicitlyResumes()
    {
        var content = new WorkflowContent([A, B]) { BlockVideo = B };
        await using var runtime = await Runtime.StartAsync(content);
        await using var assistant = await ConnectAsync(runtime.Server.Endpoint);
        try
        {
            var created = await Success(assistant, "create_playlist", new { name = "延迟准备", references = new[] { new ContentRef("video", B) } });
            var playlistId = created.GetProperty("playlistId").GetGuid();
            var taskId = created.GetProperty("taskId").GetGuid();
            await content.BlockedMetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            await Success(assistant, "load_playlist", new { playlistId, autoplay = true });
            await Success(assistant, "control_playback", new { action = "pause" });
            var pending = await Success(assistant, "get_status", new { });
            Assert.Equal("paused", pending.GetProperty("playback").GetProperty("intent").GetString());
            Assert.False(pending.GetProperty("outputActive").GetBoolean());
            content.ReleaseMetadata.TrySetResult();
            await Until(() => runtime.Library.Snapshot.Agent!.Tasks.Single(t => t.Id == taskId).State == "completed");
            var paused = await Success(assistant, "get_status", new { });
            Assert.Equal("paused", paused.GetProperty("playback").GetProperty("intent").GetString());
            Assert.False(paused.GetProperty("outputActive").GetBoolean());
            Assert.Empty(runtime.Player.Loads);
            Assert.Single(runtime.Library.Snapshot.Queue);

            await Success(assistant, "control_playback", new { action = "resume" });
            var resumed = await Success(assistant, "get_status", new { });
            Assert.True(resumed.GetProperty("outputActive").GetBoolean());
            Assert.Equal(B, resumed.GetProperty("current").GetProperty("key").GetProperty("bvid").GetString());
            Assert.Single(runtime.Player.Loads);
        }
        finally { content.ReleaseMetadata.TrySetResult(); }
    }

    private static async Task<JsonElement> Success(McpClient client, string tool, object arguments)
    {
        var result = await Invoke(client, tool, arguments);
        Assert.False(result.IsError, JsonSerializer.Serialize(result));
        return Data(result);
    }
    private static JsonElement Data(CallToolResult result) => Assert.IsType<JsonElement>(result.StructuredContent).Clone();
    private static async Task<CallToolResult> Invoke(McpClient client, string tool, object arguments)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = tool,
            Arguments = JsonSerializer.SerializeToElement(arguments, AgentJson.Options).Deserialize<Dictionary<string, JsonElement>>() }, Cancellation);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(MediaSecret, serialized);
        Assert.DoesNotContain(CookieSecret, serialized);
        Assert.DoesNotContain(AccessToken, serialized);
        return result;
    }
    private static Task<McpClient> ConnectAsync(Uri endpoint) => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + AccessToken }
    }), new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(10) }, cancellationToken: Cancellation);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Runtime : IAsyncDisposable
    {
        public WorkflowContent Content { get; }
        public MemoryStore Store { get; } = new();
        public WorkflowPlayer Player { get; } = new();
        public LibrarySession Library { get; }
        public AgentControlService Control { get; }
        public McpServerHost Server { get; private set; } = null!;
        private Runtime(WorkflowContent content)
        {
            Content = content; Library = new(content, Store, Player); Control = new(Library, content, content);
        }
        public static async Task<Runtime> StartAsync(WorkflowContent content)
        {
            var runtime = new Runtime(content);
            await runtime.Library.InitializeAsync(Cancellation);
            runtime.Server = await McpServerHost.StartAsync(runtime.Control, AvailablePort(), AccessToken, Cancellation);
            return runtime;
        }
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync(); await Control.DisposeAsync(); await Library.DisposeAsync();
        }
    }
    private sealed class MemoryStore : ILibraryStore
    {
        public LibraryState Saved { get; private set; } = new();
        public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
        {
            Saved = JsonSerializer.SerializeToElement(state, AgentJson.Options).Deserialize<LibraryState>(AgentJson.Options)!;
            return Task.CompletedTask;
        }
    }
    private sealed class WorkflowContent : IContentClient, IContentSearchClient
    {
        private readonly string[] _collection;
        private int _collectionPageCalls;
        public IReadOnlyDictionary<string, VideoInfo> Videos { get; }
        public ConcurrentQueue<ContentKey> Resolved { get; } = new();
        public string? BlockVideo { get; init; }
        public bool BlockedMetadataCancelled { get; private set; }
        public TaskCompletionSource BlockedMetadataStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMetadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CollectionPageCalls => Volatile.Read(ref _collectionPageCalls);
        public WorkflowContent(string[] collection)
        {
            _collection = collection;
            Videos = collection.Select((id, index) => new VideoInfo(id, "摄影课程 " + (index + 1), "示例讲师", 42,
                TimeSpan.FromMinutes(3), (id == A ? new[] { 11L, 12L } : id == B ? new[] { 21L } : new[] { 100L + index })
                .Select((cid, page) => new VideoPart(page + 1, new(id, cid), "第 " + (page + 1) + " 节", TimeSpan.FromMinutes(3))).ToArray()))
                .ToDictionary(v => v.Bvid);
        }
        public async Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default)
        {
            if (input == BlockVideo)
            {
                BlockedMetadataStarted.TrySetResult();
                try { await ReleaseMetadata.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { BlockedMetadataCancelled = true; throw; }
            }
            return Videos[input];
        }
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default)
        {
            Resolved.Enqueue(key);
            return Task.FromResult(new MediaSession(key, new(new Uri("https://media.invalid/video?signature=" + MediaSecret), "h264", 64), null,
                new Dictionary<string, string> { ["Cookie"] = "SESSDATA=" + CookieSecret }, DateTimeOffset.UtcNow, null));
        }
        public Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _collectionPageCalls);
            return Task.FromResult(new CollectionPage(source, "摄影系列", page, pageSize, _collection.Length,
                _collection.Skip((page - 1) * pageSize).Take(pageSize).Select(id => new CollectionEntry(id, Videos[id].Title, Videos[id].Duration)).ToArray()));
        }
        public Task<ContentSearchPage> SearchAsync(string query, string kind = "video", int page = 1, int pageSize = 20,
            string order = "relevance", CancellationToken cancellationToken = default) => Task.FromResult(new ContentSearchPage(page, pageSize,
                _collection.Length, _collection.Skip((page - 1) * pageSize).Take(pageSize)
                    .Select(id => new ContentSearchItem(new("video", id), Videos[id].Title, "示例讲师", 42)).ToArray()));
        public Task<ContentSearchPage> GetCreatorContentAsync(long uploaderId, string kind = "videos", int page = 1,
            int pageSize = 20, CancellationToken cancellationToken = default) => SearchAsync("", page: page, pageSize: pageSize, cancellationToken: cancellationToken);
    }
    private sealed class WorkflowPlayer : IQueuePlayer
    {
        public event EventHandler<QueuePlaybackEnded>? Ended;
        public ConcurrentQueue<(ContentKey Key, Guid PlaybackId)> Loads { get; } = new();
        public TimeSpan? Position { get; private set; }
        public bool Paused { get; private set; }
        public Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume,
            CancellationToken cancellationToken = default)
        {
            Loads.Enqueue((session.Content, playbackId)); Position = position; Paused = paused;
            return Task.CompletedTask;
        }
        public void FinishCurrent() => Ended?.Invoke(this, new(Loads.Last().PlaybackId, "eof"));
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) { Paused = paused; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Position);
        public Task StopAsync(CancellationToken cancellationToken = default) { Position = null; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
