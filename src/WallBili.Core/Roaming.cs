namespace WallBili.Core;

public enum PlaybackSource { Manual, Roaming }
public sealed record RoamingVideoState(CreatorVideo Video, bool IsNew, DateTimeOffset? CompletedAt = null,
    DateTimeOffset? LastPlayedAt = null, string? Failure = null);
public sealed record CreatorSyncState(bool Seeded = false, DateTimeOffset? Watermark = null,
    int NextPage = 1, DateTimeOffset? ScanStartedAt = null, DateTimeOffset? LastSuccessfulAt = null, string? Failure = null);

public sealed class RoamingAccountState
{
    public List<FollowedCreator> Subscriptions { get; set; } = [];
    public List<RoamingVideoState> Videos { get; set; } = [];
    public Dictionary<long, CreatorSyncState> Sync { get; set; } = [];
    public List<QueueItem> Queue { get; set; } = [];
    public PlaybackState Playback { get; set; } = new();
    public HashSet<string> ExcludedBvids { get; set; } = [];
    public bool RoundActive { get; set; }
    public bool ScheduleEnabled { get; set; }
    public TimeSpan ScheduleTime { get; set; } = TimeSpan.FromHours(9);
    public DateOnly? LastScheduleDate { get; set; }
    public DateOnly? LastRefreshDate { get; set; }
}

public sealed record RoamingSnapshot(long? AccountId, IReadOnlyList<FollowedCreator> Subscriptions,
    IReadOnlyList<RoamingVideoState> Videos, IReadOnlyDictionary<long, CreatorSyncState> Sync,
    bool ScheduleEnabled, TimeSpan ScheduleTime, DateOnly? LastScheduleDate, DateOnly? LastRefreshDate,
    bool PendingStart, string Status)
{
    public static RoamingSnapshot Empty { get; } = new(null, [], [], new Dictionary<long, CreatorSyncState>(),
        false, TimeSpan.FromHours(9), null, null, false, "请先登录并选择参与漫游的 UP 主");
}
