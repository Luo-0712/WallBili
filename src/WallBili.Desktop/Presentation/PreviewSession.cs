using WallBili.Core;
using WallBili.Infrastructure.Bilibili;

namespace WallBili.Desktop.Presentation;

/// <summary>Explicit offline preview only. Normal launches always use SQLite, Bilibili and mpv.</summary>
internal static class PreviewSession
{
    internal static LibraryState Seed()
    {
        string[] titles = ["晴天的午后", "沿河慢行", "雨声和钢琴", "凌晨两点", "缓慢的星期日", "窗外的风", "树影晃动", "旧唱片的傍晚"];
        int[] seconds = [2720, 1936, 1684, 3092, 2205, 2468, 1638, 2116];
        string[] uploaders = ["房间里的声音", "房间里的声音", "白噪音档案", "独立音乐选", "房间里的声音", "独立音乐选", "房间里的声音", "独立音乐选"];
        var entries = titles.Select((title, i) => new CatalogEntry(new($"BV1demo{i:00000}", i + 1), title, uploaders[i], i + 1,
            TimeSpan.FromSeconds(seconds[i]), i == 7 ? EntryAvailability.Failed : EntryAvailability.Available, i == 7 ? "视频暂时不可访问，可刷新来源重试。" : null)).ToList();
        var queue = entries.Take(6).Select(e => new QueueItem(Guid.NewGuid(), e.Key)).ToList();
        var now = DateTimeOffset.Now;
        return new()
        {
            Entries = entries, Queue = queue,
            Sources = [new("Season:1:10", "轻音乐日常", null, new(1, 10), now, [entries[0].Key, entries[1].Key, entries[4].Key, entries[6].Key], []),
                new("Season:2:20", "专注时刻", null, new(2, 20), now, [entries[3].Key, entries[5].Key, entries[7].Key], []),
                new("Season:3:30", "雨天白噪音", null, new(3, 30), now, [entries[2].Key], [])],
            Playback = new(queue[0].Id, 768, 70, PlaybackIntent.Playing, QueueMode.RepeatAll)
        };
    }

    internal sealed class Store(LibraryState state) : ILibraryStore
    {
        public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
        public Task SaveAsync(LibraryState value, CancellationToken cancellationToken = default) { state = value; return Task.CompletedTask; }
    }
    internal sealed class Content : IContentClient
    {
        public Task<VideoInfo> GetVideoAsync(string input, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new ContentAccessException(ContentFailureKind.InvalidInput, "请输入视频 BV 号或链接。");
            var entries = Seed().Entries;
            if (entries.FirstOrDefault(e => e.Key.Bvid == input) is { } entry)
            {
                if (entry.Availability == EntryAvailability.Failed) throw new ContentAccessException(ContentFailureKind.Removed, "Preview item unavailable.");
                return Task.FromResult(new VideoInfo(entry.Key.Bvid, entry.Title, entry.Uploader, 1, entry.Duration, [new(entry.Page, entry.Key, entry.Title, entry.Duration)]));
            }
            var bvid = BilibiliInput.Parse(input).Bvid;
            return Task.FromResult(new VideoInfo(bvid, "新导入的音乐合集", "示例 UP 主", 1, TimeSpan.FromMinutes(30),
                entries.Take(3).Select((e, i) => new VideoPart(i + 1, new(bvid, 1000 + i), e.Title, e.Duration)).ToArray()));
        }
        public Task<CollectionPage> GetCollectionPageAsync(CollectionSource source, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
        {
            var seed = Seed();
            if (seed.Sources.FirstOrDefault(s => s.Collection == source) is { } collection)
            {
                var entries = seed.Entries.Where(e => collection.Members.Contains(e.Key)).Select(e => new CollectionEntry(e.Key.Bvid, e.Title, e.Duration)).ToArray();
                return Task.FromResult(new CollectionPage(source, collection.Title, page, pageSize, entries.Length, entries));
            }
            return Task.FromResult(new CollectionPage(source, "示例合集", page, pageSize, 1, [new("BV1xx411c7mD", "新导入的音乐合集", TimeSpan.FromMinutes(30))]));
        }
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, CancellationToken cancellationToken = default) => ResolveMediaAsync(key, new(), cancellationToken);
        public Task<MediaSession> ResolveMediaAsync(ContentKey key, MediaSelectionPreferences preferences, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaSession(key, new(new("https://example.invalid/preview"), "h264", 80), null, new Dictionary<string, string>(), DateTimeOffset.UtcNow, null,
                new([new("80", "1080P 高清"), new("64", "720P 高清")], [new("30280", "高品质音频")], preferences.VideoQualityId ?? "80", "30280")));
    }
    internal sealed class Player : IQueuePlayer
    {
        public event EventHandler<QueuePlaybackEnded>? Ended { add { } remove { } }
        public TimeSpan Position { get; private set; }
        public bool Paused { get; private set; }
        public Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume, CancellationToken cancellationToken = default)
        { Position = position; Paused = paused; return Task.CompletedTask; }
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) { Paused = paused; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult<TimeSpan?>(Position);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
