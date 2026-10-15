namespace WallBili.Core;

public enum EntryAvailability { Unknown, Available, Failed }
public enum PlaybackIntent { Stopped, Playing, Paused }
public enum QueueMode { Sequential, RepeatAll }

/// <summary>A transient player transport failure; retry identity without marking the catalog item invalid.</summary>
public sealed class PlaybackInterruptedException() : Exception("The playback connection is temporarily unavailable.");

public sealed record CatalogEntry(ContentKey Key, string Title, string Uploader, int Page,
    TimeSpan Duration, EntryAvailability Availability = EntryAvailability.Unknown, string? Failure = null);
public sealed record ImportProblem(string Bvid, string Failure);
public sealed record CatalogSource(string Id, string Title, string? VideoInput, CollectionSource? Collection,
    DateTimeOffset RefreshedAt, IReadOnlyList<ContentKey> Members, IReadOnlyList<ImportProblem> Problems);
public sealed record QueueItem(Guid Id, ContentKey Key, int CompletedCount = 0);
public sealed record PlaybackState(Guid? CurrentId = null, double PositionSeconds = 0, double Volume = 70,
    PlaybackIntent Intent = PlaybackIntent.Stopped, QueueMode Mode = QueueMode.Sequential);

/// <summary>A persistent aggregate containing stable content identities, never media URLs or credentials.</summary>
public sealed class LibraryState
{
    public List<CatalogEntry> Entries { get; set; } = [];
    public List<CatalogSource> Sources { get; set; } = [];
    public List<QueueItem> Queue { get; set; } = [];
    public PlaybackState Playback { get; set; } = new();
}

public sealed record LibrarySnapshot(IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<CatalogSource> Sources,
    IReadOnlyList<QueueItem> Queue, PlaybackState Playback, string Notice, bool RecoveryPending = false,
    DateTimeOffset? NextRecoveryAt = null, bool OutputActive = false)
{
    public CatalogEntry? Current => Queue.FirstOrDefault(q => q.Id == Playback.CurrentId) is { } item
        ? Entries.FirstOrDefault(e => e.Key == item.Key) : null;
}

public interface ILibraryStore
{
    Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default);
}

public sealed class QueuePlaybackEnded(Guid playbackId, string reason) : EventArgs
{
    public Guid PlaybackId { get; } = playbackId;
    public string Reason { get; } = reason;
}

public interface IQueuePlayer : IAsyncDisposable
{
    event EventHandler<QueuePlaybackEnded>? Ended;
    Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume,
        CancellationToken cancellationToken = default);
    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ResetOutputAsync(CancellationToken cancellationToken = default) => StopAsync(cancellationToken);
    Task SetVideoFitAsync(bool fill, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
