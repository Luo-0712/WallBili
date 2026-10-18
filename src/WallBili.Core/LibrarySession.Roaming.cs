namespace WallBili.Core;

public sealed partial class LibrarySession
{
    private long? _roamingAccountId;
    private long _intentVersion, _roamingRevision, _roamingScheduleRevision;
    private CancellationTokenSource? _roamingLoad;
    private string? _pendingMetadataBvid;
    private string _roamingNotice = "就绪";
    private PendingRoaming? _pendingRoaming;
    private sealed record PendingRoaming(long AccountId, long Intent, Guid? Boundary,
        bool Ready = false, bool Reached = false);
    private List<QueueItem> ManualItems => _state.ActiveSource == PlaybackSource.Manual ? _state.Queue : _state.ManualQueue;
    private RoamingAccountState? CurrentRoaming => _roamingAccountId is { } id
        ? _state.RoamingAccounts.GetValueOrDefault(id) : null;
    private RoamingAccountState? ActiveRoaming => _state.ActiveSource == PlaybackSource.Roaming &&
        _state.ActiveRoamingAccountId is { } id ? _state.RoamingAccounts.GetValueOrDefault(id) : null;
    public long RoamingRevision => Interlocked.Read(ref _roamingRevision);
    public long RoamingScheduleRevision => Interlocked.Read(ref _roamingScheduleRevision);
    public long RegisterRoamingIntent()
    {
        var version = Interlocked.Increment(ref _intentVersion);
        try { _roamingLoad?.Cancel(); } catch (ObjectDisposedException) { }
        return version;
    }
    private Task UserActionAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        RegisterRoamingIntent();
        return ExecuteAsync(ct => { _pendingRoaming = null; return action(ct); }, token);
    }
    private void SaveActiveContext()
    {
        if (ActiveRoaming is { } roaming)
        { roaming.Queue = _state.Queue.ToList(); roaming.Playback = _state.Playback; }
        else if (_state.ActiveSource == PlaybackSource.Manual)
        { _state.ManualQueue = _state.Queue.ToList(); _state.ManualPlayback = _state.Playback; }
        var before = _snapshot.ManualQueue ?? _snapshot.Queue;
        if (!before.Select(q => (q.Id, q.Key)).SequenceEqual(ManualItems.Select(q => (q.Id, q.Key))))
            _state.Agent.QueueRevision++;
    }
    private RoamingSnapshot CreateRoamingSnapshot()
    {
        if (CurrentRoaming is not { } r) return RoamingSnapshot.Empty;
        return new(_roamingAccountId, r.Subscriptions.ToArray(), r.Videos.ToArray(),
            new Dictionary<long, CreatorSyncState>(r.Sync), r.ScheduleEnabled, r.ScheduleTime,
            r.LastScheduleDate, r.LastRefreshDate, _pendingRoaming is { } p && p.Intent == Interlocked.Read(ref _intentVersion),
            _pendingRoaming is { } waiting && waiting.Intent == Interlocked.Read(ref _intentVersion)
                ? "当前视频播完后开始漫游" : _roamingNotice);
    }
    public Task SetRoamingAccountAsync(long? accountId, CancellationToken token = default) => UserActionAsync(async ct =>
    {
        if (accountId is <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        Interlocked.Increment(ref _roamingRevision);
        Interlocked.Increment(ref _roamingScheduleRevision);
        if (_state.ActiveSource == PlaybackSource.Roaming && _state.ActiveRoamingAccountId != accountId)
        {
            await SwitchToManualAsync(ct);
            _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
        }
        _roamingAccountId = accountId;
        if (accountId is { } id) _state.RoamingAccounts.TryAdd(id, new());
        if (accountId is not null && ActiveRoaming is not null && _activePlayback is null &&
            _state.Playback.Intent != PlaybackIntent.Stopped)
        {
            if (CurrentIndex() >= 0) await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds,
                _state.Playback.Intent == PlaybackIntent.Paused, ct);
            else if (_state.Playback.Intent == PlaybackIntent.Playing) await StartNextRoamingVideoAsync(ct);
        }
        _roamingNotice = accountId is null ? "请登录后使用订阅漫游" : "已读取本账号订阅";
    }, token);

    public Task SetSubscriptionsAsync(IEnumerable<FollowedCreator> creators, CancellationToken token = default) =>
        ExecuteAsync(ct =>
        {
            var r = CurrentRoaming ?? throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "Sign in first.");
            var selected = creators.ToList();
            if (selected.Any(c => c.Uid <= 0 || string.IsNullOrWhiteSpace(c.Name))) throw new ArgumentException("Invalid creator.");
            r.Subscriptions = selected.DistinctBy(c => c.Uid).ToList();
            _pendingRoaming = null;
            Interlocked.Increment(ref _roamingRevision);
            Interlocked.Increment(ref _roamingScheduleRevision);
            r.LastRefreshDate = null;
            _roamingNotice = $"已订阅 {r.Subscriptions.Count} 位 UP 主";
            return Task.CompletedTask;
        }, token);

    public Task MergeRoamingPageAsync(long accountId, long uploaderId, IEnumerable<CreatorVideo> videos, bool completed,
        int nextPage = 1, DateTimeOffset? scanStartedAt = null, long? expectedRevision = null, CancellationToken token = default) =>
        ExecuteAsync(ct =>
        {
            if (_roamingAccountId != accountId || expectedRevision is { } revision && revision != RoamingRevision ||
                CurrentRoaming is not { } r || !r.Subscriptions.Any(s => s.Uid == uploaderId)) return Task.CompletedTask;
            var sync = r.Sync.GetValueOrDefault(uploaderId) ?? new();
            var started = scanStartedAt ?? _time.GetUtcNow();
            foreach (var video in videos)
            {
                if (video.UploaderId != uploaderId || string.IsNullOrWhiteSpace(video.Bvid))
                    throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Creator identity mismatch.");
                var index = r.Videos.FindIndex(v => v.Video.Bvid == video.Bvid);
                if (index < 0) r.Videos.Add(new(video, video.PublishedAt >= (sync.Seeded
                    ? sync.Watermark ?? started : started.AddDays(-7))));
                else r.Videos[index] = r.Videos[index] with { Video = video, Failure = null };
            }
            r.Sync[uploaderId] = completed
                ? new(true, started, 1, null, _time.GetUtcNow())
                : sync with { NextPage = nextPage, ScanStartedAt = started, Failure = null };
            _roamingNotice = $"已整理 {r.Videos.Count} 个订阅视频";
            return Task.CompletedTask;
        }, token);

    public Task SetRoamingRefreshResultAsync(long accountId, long uploaderId, string? failure,
        bool allCompleted = false, long? expectedRevision = null, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        if (_roamingAccountId != accountId || expectedRevision is { } revision && revision != RoamingRevision || CurrentRoaming is not { } r)
            return Task.CompletedTask;
        if (uploaderId > 0)
            r.Sync[uploaderId] = (r.Sync.GetValueOrDefault(uploaderId) ?? new()) with { Failure = failure };
        if (allCompleted) r.LastRefreshDate = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        return Task.CompletedTask;
    }, token);

    public Task SetRoamingScheduleAsync(bool enabled, TimeSpan time, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(time));
        var r = CurrentRoaming ?? throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "Sign in first.");
        r.ScheduleEnabled = enabled; r.ScheduleTime = time;
        Interlocked.Increment(ref _roamingScheduleRevision);
        _pendingRoaming = null;
        return Task.CompletedTask;
    }, token);

    public Task CancelPendingRoamingAsync(CancellationToken token = default) => UserActionAsync(ct => Task.CompletedTask, token);

    public async Task<long?> TryBeginScheduledRoamingAsync(long accountId, DateOnly date, TimeSpan scheduledTime,
        long scheduleRevision, CancellationToken token = default)
    {
        long? request = null;
        await ExecuteAsync(ct =>
        {
            if (_roamingAccountId != accountId || scheduleRevision != RoamingScheduleRevision ||
                CurrentRoaming is not { ScheduleEnabled: true } r || r.Subscriptions.Count == 0 || r.ScheduleTime != scheduledTime ||
                r.LastScheduleDate == date || _suspended) return Task.CompletedTask;
            r.LastScheduleDate = date;
            if (_state.Playback.Intent == PlaybackIntent.Paused ||
                _state.ActiveSource == PlaybackSource.Roaming) return Task.CompletedTask;
            var index = CurrentIndex();
            Guid? boundary = null;
            if (_state.Playback.Intent == PlaybackIntent.Playing && index >= 0)
            {
                var bvid = _state.Queue[index].Key.Bvid;
                while (index + 1 < _state.Queue.Count && _state.Queue[index + 1].Key.Bvid == bvid) index++;
                boundary = _state.Queue[index].Id;
            }
            request = Interlocked.Read(ref _intentVersion);
            _pendingRoaming = new(_roamingAccountId!.Value, request.Value, boundary, Reached: boundary is null);
            return Task.CompletedTask;
        }, token);
        return request;
    }

    public Task CompleteScheduledRoamingAsync(long intent, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_pendingRoaming is not { } pending || pending.Intent != intent || intent != Interlocked.Read(ref _intentVersion) ||
            pending.AccountId != _roamingAccountId || _suspended) return;
        _pendingRoaming = pending with { Ready = true };
        if (pending.Reached) { _pendingRoaming = null; await BeginRoamingAsync(true, ct); }
    }, token);

    private async Task<bool> TryScheduledBoundaryAsync(Guid ended, int next, CancellationToken ct)
    {
        if (_pendingRoaming is not { } pending || pending.Intent != Interlocked.Read(ref _intentVersion) ||
            pending.AccountId != _roamingAccountId || pending.Boundary != ended) return false;
        _state.Playback = _state.Playback with { CurrentId = next < _state.Queue.Count ? _state.Queue[next].Id : null,
            PositionSeconds = 0, Intent = PlaybackIntent.Stopped };
        _pendingRoaming = pending with { Reached = true };
        if (pending.Ready) { _pendingRoaming = null; await BeginRoamingAsync(true, ct); }
        return true;
    }

    public Task StartRoamingAsync(long? expectedIntentVersion = null, bool newRound = false, CancellationToken token = default)
    {
        var intent = expectedIntentVersion ?? RegisterRoamingIntent();
        return ExecuteAsync(async ct =>
        {
            if (intent != Interlocked.Read(ref _intentVersion) || _suspended) return;
            _pendingRoaming = null;
            await BeginRoamingAsync(newRound, ct);
        }, token);
    }
    private async Task BeginRoamingAsync(bool newRound, CancellationToken ct)
    {
        DetachPreparedQueues();
        var r = CurrentRoaming ?? throw new ContentAccessException(ContentFailureKind.AuthenticationRequired, "Sign in first.");
        if (_state.ActiveSource != PlaybackSource.Roaming)
        {
            await CapturePositionAsync(ct); await StopPlayerAsync(ct); SaveActiveContext();
            var volume = _state.Playback.Volume;
            _state.ActiveSource = PlaybackSource.Roaming; _state.ActiveRoamingAccountId = _roamingAccountId;
            _state.Queue = r.Queue.ToList(); _state.Playback = r.Playback with { Volume = volume, Mode = QueueMode.Sequential };
        }
        if (newRound || !r.RoundActive)
        {
            r.ExcludedBvids.Clear(); r.RoundActive = true;
            _state.Queue.Clear(); _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0 };
        }
        if (CurrentIndex() >= 0 && !newRound)
            await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, false, ct);
        else await StartNextRoamingVideoAsync(ct);
    }

    public Task PlayManualAsync(Guid? id = null, CancellationToken token = default) => UserActionAsync(async ct =>
    {
        DetachPreparedQueues();
        await SwitchToManualAsync(ct);
        var index = id is { } target ? _state.Queue.FindIndex(q => q.Id == target) : CurrentIndex();
        if (index < 0) index = 0;
        await StartAsync(index, id is null ? _state.Playback.PositionSeconds : 0, false, ct);
    }, token);
    private async Task SwitchToManualAsync(CancellationToken ct)
    {
        if (_state.ActiveSource == PlaybackSource.Manual) return;
        await CapturePositionAsync(ct); await StopPlayerAsync(ct); SaveActiveContext();
        var volume = _state.Playback.Volume;
        _state.Queue = _state.ManualQueue.ToList(); _state.Playback = _state.ManualPlayback with { Volume = volume };
        _state.ActiveSource = PlaybackSource.Manual; _state.ActiveRoamingAccountId = null;
        _recoveryPending = false; _pendingMetadataBvid = null;
    }
    private void CompleteRoamingVideo(string bvid)
    {
        if (ActiveRoaming is not { } r) return;
        var index = r.Videos.FindIndex(v => v.Video.Bvid == bvid);
        if (index < 0) return;
        if (_state.Queue.All(q => q.CompletedCount > 0)) r.Videos[index] = r.Videos[index] with { CompletedAt = _time.GetUtcNow() };
        else r.ExcludedBvids.Add(bvid);
    }
    private async Task StartNextRoamingVideoAsync(CancellationToken ct)
    {
        var r = ActiveRoaming;
        if (r is null) return;
        await StopPlayerAsync(ct);
        var previousBvid = _state.Queue.FirstOrDefault()?.Key.Bvid;
        var intent = Interlocked.Read(ref _intentVersion);
        var attempted = new HashSet<string>();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _roamingLoad = load;
        try
        {
            while (true)
            {
                var last = previousBvid;
                var candidates = r.Videos.Where(v => r.Subscriptions.Any(s => s.Uid == v.Video.UploaderId) &&
                    !r.ExcludedBvids.Contains(v.Video.Bvid) && v.Failure is null && !attempted.Contains(v.Video.Bvid)).ToArray();
                var eligible = candidates.Any(v => v.Video.Bvid != last) ? candidates.Where(v => v.Video.Bvid != last) : candidates;
                var selected = eligible.FirstOrDefault(v => v.Video.Bvid == _pendingMetadataBvid) ??
                    eligible.OrderBy(v => v.CompletedAt is null ? v.IsNew ? 0 : 1 : 2)
                    .ThenByDescending(v => v.CompletedAt is null ? v.Video.PublishedAt : DateTimeOffset.MinValue)
                    .ThenBy(v => v.CompletedAt is null ? DateTimeOffset.MinValue : v.LastPlayedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(v => v.Video.Bvid, StringComparer.Ordinal).FirstOrDefault();
                if (selected is null) break;
                var bvid = selected.Video.Bvid;
                attempted.Add(bvid); _pendingMetadataBvid = bvid;
                _state.Queue.Clear();
                _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0, Intent = PlaybackIntent.Playing };
                try
                {
                    var parts = _state.Entries.Where(e => e.Key.Bvid == bvid).OrderBy(e => e.Page).ToArray();
                    if (parts.Length == 0)
                    {
                        var video = await _content.GetVideoAsync(bvid, load.Token);
                        if (intent != Interlocked.Read(ref _intentVersion)) return;
                        parts = video.Parts.Select(p => new CatalogEntry(p.Key,
                            video.Parts.Count == 1 ? video.Title : $"{video.Title} · {p.Title}", video.Uploader, p.Page, p.Duration)).ToArray();
                        _state.Entries.AddRange(parts.Where(p => !_state.Entries.Any(e => e.Key == p.Key)));
                    }
                    if (intent != Interlocked.Read(ref _intentVersion)) return;
                    _state.Queue = parts.Select(p => new QueueItem(Guid.NewGuid(), p.Key)).ToList();
                    _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0, Mode = QueueMode.Sequential };
                    _pendingMetadataBvid = null;
                    var index = r.Videos.FindIndex(v => v.Video.Bvid == bvid);
                    var latest = r.Videos.Max(v => v.LastPlayedAt) ?? DateTimeOffset.MinValue;
                    var playedAt = _time.GetUtcNow();
                    if (playedAt <= latest) playedAt = latest.AddTicks(1);
                    r.Videos[index] = r.Videos[index] with { LastPlayedAt = playedAt };
                    await StartAsync(0, 0, false, load.Token);
                    if (_state.Playback.Intent != PlaybackIntent.Stopped) return;
                    r.Videos[index] = r.Videos[index] with { Failure = "不可播放" };
                }
                catch (ContentAccessException ex) when (ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited)
                { await ScheduleRecoveryAsync("订阅视频暂时无法获取", ct); return; }
                catch (ContentAccessException ex) when (ex.Kind == ContentFailureKind.AuthenticationRequired)
                {
                    _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                    _roamingNotice = _notice = "需要重新登录或相应权益，已暂停漫游"; return;
                }
                catch (ContentAccessException ex)
                {
                    var index = r.Videos.FindIndex(v => v.Video.Bvid == bvid);
                    r.Videos[index] = r.Videos[index] with { Failure = ex.Kind.ToString() };
                }
            }
            _recoveryPending = false; _pendingMetadataBvid = null; r.RoundActive = false;
            _state.Playback = _state.Playback with { CurrentId = null, Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
            _roamingNotice = _notice = "暂无可播订阅内容，或本轮内容均已跳过；可刷新或重新开始";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && load.IsCancellationRequested) { }
        finally { _roamingLoad = null; }
    }

    private static PlaybackState RemoveInactiveItems(List<QueueItem> queue, PlaybackState playback, Predicate<QueueItem> remove)
    {
        var current = queue.FindIndex(q => q.Id == playback.CurrentId);
        var removedCurrent = current >= 0 && remove(queue[current]);
        var successor = current < 0 ? 0 : queue.Take(current).Count(q => !remove(q));
        queue.RemoveAll(remove);
        return removedCurrent ? playback with { CurrentId = successor < queue.Count ? queue[successor].Id : null,
            PositionSeconds = 0, Intent = successor < queue.Count ? playback.Intent : PlaybackIntent.Stopped } : playback;
    }
}
