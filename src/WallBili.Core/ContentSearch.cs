namespace WallBili.Core;

/// <summary>Metadata-only discovery. References can be passed directly to library and playlist actions.</summary>
public sealed record ContentSearchItem(ContentRef Reference, string Title, string? Uploader = null,
    long? UploaderId = null, TimeSpan? Duration = null, DateTimeOffset? PublishedAt = null);

public sealed record ContentSearchPage(int Page, int PageSize, int Total, IReadOnlyList<ContentSearchItem> Items)
{
    public bool HasMore => (long)Page * PageSize < Total;
}

public interface IContentSearchClient
{
    /// <summary>Search public videos or creators; no logged-in account is required.</summary>
    Task<ContentSearchPage> SearchAsync(string query, string kind = "video", int page = 1, int pageSize = 20,
        string order = "relevance", CancellationToken cancellationToken = default);

    /// <summary>Browse public creator videos or native collections (seasons and series).</summary>
    Task<ContentSearchPage> GetCreatorContentAsync(long uploaderId, string kind = "videos", int page = 1,
        int pageSize = 20, CancellationToken cancellationToken = default);
}
