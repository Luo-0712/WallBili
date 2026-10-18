namespace WallBili.Core;

public sealed record FollowedCreator(long Uid, string Name);
public sealed record FollowingsPage(int Page, int PageSize, int Total, IReadOnlyList<FollowedCreator> Items)
{
    public bool HasMore => (long)Page * PageSize < Total;
}
public sealed record CreatorVideo(string Bvid, long UploaderId, string Uploader, string Title,
    DateTimeOffset PublishedAt, TimeSpan Duration);
public sealed record CreatorVideosPage(long UploaderId, int Page, int PageSize, int Total,
    IReadOnlyList<CreatorVideo> Items)
{
    public bool HasMore => (long)Page * PageSize < Total;
}
public interface IContentDiscoveryClient
{
    Task<FollowingsPage> GetFollowingsAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default);
    Task<CreatorVideosPage> GetCreatorVideosAsync(long uploaderId, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default);
}
