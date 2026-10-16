using WallBili.Core;
using WallBili.Infrastructure.Storage;

namespace WallBili.Library.Tests;

internal sealed class LibraryFixture : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "wallbili-library-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(DirectoryPath, "library.db");
    public TestContent Content { get; } = new();
    public TestPlayer Player { get; } = new();
    public SqliteLibraryStore Store { get; }
    public LibrarySession Session { get; }
    public LibraryFixture(TimeProvider? time = null, MediaSelectionPreferences? selection = null)
    {
        Store = new(DatabasePath);
        Session = new(Content, Store, Player, time, selection);
    }
    public async Task SeedAsync()
    {
        await Session.InitializeAsync();
        await Session.ImportCollectionAsync(new(1, 10));
        await Session.ImportCollectionAsync(new(2, 20));
        await Session.EnqueueAsync(Session.Snapshot.Entries.Select(e => e.Key));
    }
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

internal sealed class TestContent : IContentClient
{
    public const string A = "BV1test000001", B = "BV1test000002", C = "BV1test000003", D = "BV1test000004";
    public Dictionary<string, VideoInfo> Videos { get; } = new()
    {
        [A] = Video(A, 11, 12), [B] = Video(B, 21), [C] = Video(C, 31), [D] = Video(D, 41)
    };
    public Dictionary<long, List<string>> Collections { get; } = new() { [10] = [A, B], [20] = [C] };
    public Dictionary<string, ContentFailureKind> MetadataFailures { get; } = [];
    public HashSet<ContentKey> MediaFailures { get; } = [];
    public Dictionary<ContentKey, ContentFailureKind> ResolveFailures { get; } = [];
    public List<ContentKey> Resolved { get; } = [];
    public List<MediaSelectionPreferences> Selections { get; } = [];
    public Func<ContentKey, MediaSelectionPreferences, MediaSession>? CustomResolve { get; set; }
    public Uri MediaUri { get; set; } = new("https://example.invalid/media?temporary-secret");
    public Dictionary<ContentKey, Uri> MediaUris { get; } = [];
    public bool RepeatedPage { get; set; }
    public Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default)
    {
        if (MetadataFailures.TryGetValue(input, out var failure)) throw new ContentAccessException(failure, "Test metadata failure");
        return Task.FromResult(Videos[input]);
    }
    public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default) =>
        ResolveMediaAsync(key, MediaSelectionPreferences.Automatic, cancellationToken);
    public Task<MediaSession> ResolveMediaAsync(ContentKey key, MediaSelectionPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        Resolved.Add(key);
        Selections.Add(preferences);
        if (CustomResolve is { } resolve) return Task.FromResult(resolve(key, preferences));
        if (ResolveFailures.TryGetValue(key, out var failure)) throw new ContentAccessException(failure, "Test resolve failure");
        if (MediaFailures.Contains(key)) throw new ContentAccessException(ContentFailureKind.Removed, "Test missing content");
        return Task.FromResult(new MediaSession(key, new(MediaUris.GetValueOrDefault(key, MediaUri), "h264", 16), null,
            new Dictionary<string, string>(), DateTimeOffset.UtcNow, null));
    }
    public Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        var items = Collections[source.CollectionId];
        var bvid = items[RepeatedPage ? 0 : page - 1];
        return Task.FromResult(new CollectionPage(source, "Source " + source.CollectionId, page, 1, items.Count,
            [new(bvid, bvid, TimeSpan.FromSeconds(60))]));
    }
    private static VideoInfo Video(string bvid, params long[] cids) => new(bvid, "Video " + bvid, "Uploader", 1,
        TimeSpan.FromSeconds(60 * cids.Length), cids.Select((cid, i) => new VideoPart(i + 1, new(bvid, cid),
            "Part " + (i + 1), TimeSpan.FromSeconds(60))).ToArray());
}

internal sealed class TestPlayer : IQueuePlayer
{
    public event EventHandler<QueuePlaybackEnded>? Ended;
    public List<(ContentKey Key, Guid Generation, TimeSpan Position, bool Paused, double Volume, MediaSession Session)> Loads { get; } = [];
    public TimeSpan? Position { get; set; }
    public bool Paused { get; private set; }
    public bool FailLoad { get; set; }
    public int FailLoadsRemaining { get; set; }
    public bool EndDuringNextLoad { get; set; }
    public Guid Generation => Loads[^1].Generation;
    public Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume,
        CancellationToken cancellationToken = default)
    {
        if (FailLoad || FailLoadsRemaining > 0)
        {
            if (FailLoadsRemaining > 0) FailLoadsRemaining--;
            throw new InvalidOperationException("Player unavailable");
        }
        Loads.Add((session.Content, playbackId, position, paused, volume, session));
        Position = position; Paused = paused;
        if (EndDuringNextLoad) { EndDuringNextLoad = false; End(); }
        return Task.CompletedTask;
    }
    public void End(string reason = "eof", Guid? generation = null) => Ended?.Invoke(this, new(generation ?? Generation, reason));
    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) { Paused = paused; return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Position);
    public Task StopAsync(CancellationToken cancellationToken = default) { Position = null; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
