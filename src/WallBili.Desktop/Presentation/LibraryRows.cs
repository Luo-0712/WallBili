using WallBili.Core;

namespace WallBili.Desktop.Presentation;

public sealed class VideoRow : ObservableObject
{
    private bool _selected, _current;
    private string _status = "待播放";
    private readonly Action _selectionChanged;
    internal VideoRow(CatalogEntry entry, Action selectionChanged) { Entry = entry; _selectionChanged = selectionChanged; }
    public CatalogEntry Entry { get; private set; }
    public ContentKey Key => Entry.Key;
    public string Title => Entry.Title;
    public string Uploader => Entry.Uploader;
    public string Bvid => Entry.Key.Bvid;
    public int Page => Entry.Page;
    public string Duration => DisplayText.Time(Entry.Duration.TotalSeconds);
    public string Number { get; internal set; } = "";
    public string Source { get; internal set; } = "";
    public string Failure => Entry.Failure ?? "";
    public bool IsFailed => Entry.Availability == EntryAvailability.Failed;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) _selectionChanged(); } }
    public bool IsCurrent { get => _current; private set => Set(ref _current, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    internal void Update(CatalogEntry entry, int number, string source, LibrarySnapshot snapshot)
    {
        Entry = entry; Number = number.ToString("00"); Source = source;
        IsCurrent = snapshot.Current?.Key == Key;
        Status = IsFailed ? "不可访问" : IsCurrent ? DisplayText.Intent(snapshot) : "待播放";
        Changed(nameof(Entry), nameof(Title), nameof(Uploader), nameof(Duration), nameof(Number), nameof(Source), nameof(Failure), nameof(IsFailed), nameof(Page));
    }
}

public sealed class QueueRow : ObservableObject
{
    public Guid Id { get; }
    public ContentKey Key { get; }
    public string Title { get; private set; } = "";
    public string Uploader { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string Number { get; private set; } = "";
    public string Duration { get; private set; } = "";
    public string Status { get; private set; } = "";
    public bool IsCurrent { get; private set; }
    internal QueueRow(QueueItem item) { Id = item.Id; Key = item.Key; }
    internal void Update(QueueItem item, CatalogEntry? entry, int number, string source, LibrarySnapshot snapshot)
    {
        Title = entry?.Title ?? "片库中已移除的内容"; Uploader = entry?.Uploader ?? "";
        Source = source; Number = number.ToString("00"); Duration = DisplayText.Time(entry?.Duration.TotalSeconds ?? 0);
        IsCurrent = snapshot.Playback.CurrentId == item.Id;
        Status = entry?.Availability == EntryAvailability.Failed ? "不可访问" : IsCurrent ? DisplayText.Intent(snapshot) : "待播放";
        Changed(nameof(Title), nameof(Uploader), nameof(Source), nameof(Number), nameof(Duration), nameof(Status), nameof(IsCurrent));
    }
}

public sealed record SourceRow(string Id, string Title, string Uploader, int Count, string Detail, IReadOnlyList<ContentKey> Members);
public sealed record SelectOption(string Id, string Label);
public sealed record DisplayChoice(DisplayTarget Target, int Number)
{
    public string Id => Target.Id;
    public bool Primary => Target.Primary;
    public string ShortLabel => $"显示器 {Number}" + (Primary ? " · 主屏" : "");
    public string Label => $"{ShortLabel} · {Target.Width} × {Target.Height}";
}

internal static class DisplayText
{
    public static string Time(double seconds)
    {
        var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }
    public static string Intent(LibrarySnapshot snapshot) => snapshot.RecoveryPending ? "等待恢复" : snapshot.Playback.Intent switch
    { PlaybackIntent.Playing => "播放中", PlaybackIntent.Paused => "已暂停", _ => "已停止" };
}
