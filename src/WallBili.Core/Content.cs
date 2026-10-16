namespace WallBili.Core;

/// <summary>Stable identity persisted by catalogs and queues. Media URLs are never identities.</summary>
public sealed record ContentKey(string Bvid, long Cid)
{
    public string Platform => "bilibili";
}

public sealed record VideoPart(int Page, ContentKey Key, string Title, TimeSpan Duration);

public sealed record VideoInfo(string Bvid, string Title, string Uploader, long UploaderId,
    TimeSpan Duration, IReadOnlyList<VideoPart> Parts);

public sealed record MediaTrack(Uri Url, string Codec, int Quality, int Width = 0, int Height = 0, int Bandwidth = 0)
{
    // Signed URLs can contain temporary access tokens; accidental structured logging should be safe.
    public override string ToString() => $"{Codec} quality={Quality} {Width}x{Height}";
}

public sealed record MediaSession(ContentKey Content, MediaTrack Video, MediaTrack? Audio,
    IReadOnlyDictionary<string, string> Headers, DateTimeOffset ResolvedAt, DateTimeOffset? ExpiresAt,
    MediaSelectionSummary? Selection = null)
{
    public override string ToString() => $"{Content} video=({Video}) audio=({Audio}) resolved={ResolvedAt:O}";
}

public enum CollectionKind { Season, Series }

public sealed record CollectionSource(long UploaderId, long CollectionId, CollectionKind Kind = CollectionKind.Season);

/// <summary>Collection listing identity. GetVideoAsync expands each entry into authoritative CIDs.</summary>
public sealed record CollectionEntry(string Bvid, string Title, TimeSpan Duration);

public sealed record CollectionPage(CollectionSource Source, string Title, int Page, int PageSize, int Total,
    IReadOnlyList<CollectionEntry> Entries)
{
    public bool HasMore => (long)Page * PageSize < Total;
}

public interface IContentClient
{
    Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default);
    Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default);

    /// <summary>Resolves media honouring an explicit quality preference. Default implementations
    /// fall back to the automatic selection of the two-argument overload.</summary>
    Task<MediaSession> ResolveMediaAsync(ContentKey key, MediaSelectionPreferences preferences,
        CancellationToken cancellationToken = default) => ResolveMediaAsync(key, cancellationToken);

    Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default);
}
