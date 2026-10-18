namespace WallBili.Core;

/// <summary>Serializes queue edits, EOF callbacks and checkpoints. A failed traversal visits each item at most once.</summary>
public sealed partial class LibrarySession : IAsyncDisposable
{
    private readonly IContentClient _content;
    private readonly ILibraryStore _store;
    private readonly IQueuePlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _time;
    private bool _recoveryPending, _suspended;
    private int _retryAttempt, _streamFailures;
    private DateTimeOffset _retryAt, _lastAdvance;
    private double _lastPosition;
    private LibraryState _state = new();
    private Guid? _activePlayback;
    private bool _disposed;
    private bool _initialized;
    private string _notice = "就绪";
    private LibrarySnapshot _snapshot = new([], [], [], new(), "就绪");
    private MediaSelectionPreferences _selection = MediaSelectionPreferences.Automatic;
    private MediaSelectionSummary? _media;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _sourceRemovalRevisions = new();

    public LibrarySession(IContentClient content, ILibraryStore store, IQueuePlayer player,
        TimeProvider? time = null, MediaSelectionPreferences? selection = null)
    {
        _content = content; _store = store; _player = player;
        _time = time ?? TimeProvider.System;
        _selection = selection ?? MediaSelectionPreferences.Automatic;
        _player.Ended += Player_Ended;
    }

    public LibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsInitialized => _initialized;
    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_initialized) throw new InvalidOperationException("The library is already initialized.");
        _state = await _store.LoadAsync(ct);
        for (var i = 0; i < _state.Agent.Tasks.Count; i++)
            if (_state.Agent.Tasks[i].State is "pending" or "running")
                _state.Agent.Tasks[i] = _state.Agent.Tasks[i] with { State = "interrupted", Failure = "Interrupted" };
        _initialized = true;
        if (_state.Playback.CurrentId is { } id && !_state.Queue.Any(q => q.Id == id))
            _state.Playback = _state.Playback with { CurrentId = null, Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
        _notice = "已恢复本地目录与队列";
        if (_state.ActiveSource == PlaybackSource.Manual && _state.Playback.CurrentId is not null && _state.Playback.Intent != PlaybackIntent.Stopped)
            await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent == PlaybackIntent.Paused, ct);
    }, token, initializing: true);

    public async Task ImportVideoAsync(string input, CancellationToken token = default)
    {
        var removals = _sourceRemovalRevisions.ToArray().ToDictionary(p => p.Key, p => p.Value);
        await ApplyImportAsync(await new CatalogImporter(_content).VideoAsync(input, token), removals, token);
    }
    public async Task ImportCollectionAsync(CollectionSource source, CancellationToken token = default)
    {
        var removals = _sourceRemovalRevisions.ToArray().ToDictionary(p => p.Key, p => p.Value);
        await ApplyImportAsync(await new CatalogImporter(_content).CollectionAsync(source, token), removals, token);
    }

    public async Task RefreshSourceAsync(string id, CancellationToken token = default)
    {
        var source = Snapshot.Sources.First(s => s.Id == id);
        if (source.Collection is { } collection) await ImportCollectionAsync(collection, token);
        else await ImportVideoAsync(source.VideoInput!, token);
    }

    private Task ApplyImportAsync(CatalogImport import, IReadOnlyDictionary<string, long> removals, CancellationToken token) => ExecuteAsync(ct =>
    {
        if (_sourceRemovalRevisions.GetValueOrDefault(import.Source.Id) != removals.GetValueOrDefault(import.Source.Id))
            throw new AgentControlException("Conflict", "The source was removed while import was in progress.");
        _state.Agent.RemovedSources.Remove(import.Source.Id);
        foreach (var entry in import.Entries)
        {
            var index = _state.Entries.FindIndex(e => e.Key == entry.Key);
            if (index < 0) _state.Entries.Add(entry);
            else _state.Entries[index] = entry with
            {
                Availability = _state.Entries[index].Availability == EntryAvailability.Available
                    ? EntryAvailability.Available : EntryAvailability.Unknown
            };
        }
        var sourceIndex = _state.Sources.FindIndex(s => s.Id == import.Source.Id);
        var source = import.Source;
        if (sourceIndex < 0) _state.Sources.Add(source);
        else
        {
            // Failed metadata expansion retains previously known CIDs; unknown CIDs are never invented.
            var failedBvids = source.Problems.Select(p => p.Bvid).ToHashSet();
            source = source with { Members = source.Members.Concat(_state.Sources[sourceIndex].Members
                .Where(k => failedBvids.Contains(k.Bvid))).Distinct().ToArray() };
            _state.Sources[sourceIndex] = source;
        }
        _notice = $"已导入/刷新 {source.Title}：{source.Members.Count} 个分 P，{source.Problems.Count} 个不可访问视频";
        return Task.CompletedTask;
    }, token);

    public Task RemoveSourceAsync(string id, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        _sourceRemovalRevisions.AddOrUpdate(id, 1, (_, revision) => revision + 1);
        _state.Sources.RemoveAll(s => s.Id == id);
        if (!_state.Agent.RemovedSources.Contains(id, StringComparer.Ordinal)) _state.Agent.RemovedSources.Add(id);
        _notice = "已移除来源，目录与队列保留";
        return Task.CompletedTask;
    }, token);

    public Task EnqueueAsync(IEnumerable<ContentKey> keys, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        var selected = keys.ToArray();
        if (selected.Any(key => !_state.Entries.Any(e => e.Key == key))) throw new ArgumentException("Content is not in the catalog.");
        foreach (var key in selected)
        {
            ManualItems.Add(new(Guid.NewGuid(), key));
        }
        _notice = "已加入队列";
        return Task.CompletedTask;
    }, token);

    public Task MoveQueueAsync(Guid id, int offset, CancellationToken token = default) => UserActionAsync(ct =>
    {
        Move(ManualItems, ManualItems.FindIndex(q => q.Id == id), offset);
        return Task.CompletedTask;
    }, token);

    public Task MoveEntryAsync(ContentKey key, int offset, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        Move(_state.Entries, _state.Entries.FindIndex(e => e.Key == key), offset);
        return Task.CompletedTask;
    }, token);

    public Task RemoveQueueAsync(Guid id, CancellationToken token = default) => UserActionAsync(async ct =>
    {
        if (_state.ActiveSource == PlaybackSource.Roaming)
        {
            _state.ManualPlayback = RemoveInactiveItems(_state.ManualQueue, _state.ManualPlayback, q => q.Id == id);
            return;
        }
        var index = _state.Queue.FindIndex(q => q.Id == id);
        if (index < 0) return;
        var current = _state.Playback.CurrentId == id;
        var intent = _state.Playback.Intent;
        _state.Queue.RemoveAt(index);
        if (current) await AfterCurrentRemovedAsync(index, intent, ct);
        _notice = "已从队列移除";
    }, token);

    public Task RemoveEntryAsync(ContentKey key, CancellationToken token = default) => UserActionAsync(async ct =>
    {
        PruneAgentContent(key);
        var current = CurrentIndex();
        var removesCurrent = current >= 0 && _state.Queue[current].Key == key;
        var successor = current < 0 ? 0 : _state.Queue.Take(current).Count(q => q.Key != key);
        var intent = _state.Playback.Intent;
        _state.Queue.RemoveAll(q => q.Key == key);
        _state.Entries.RemoveAll(e => e.Key == key);
        _state.ManualPlayback = RemoveInactiveItems(_state.ManualQueue, _state.ManualPlayback, q => q.Key == key);
        foreach (var account in _state.RoamingAccounts.Values)
            account.Playback = RemoveInactiveItems(account.Queue, account.Playback, q => q.Key == key);
        for (var i = 0; i < _state.Sources.Count; i++)
            _state.Sources[i] = _state.Sources[i] with { Members = _state.Sources[i].Members.Where(k => k != key).ToArray() };
        if (removesCurrent) await AfterCurrentRemovedAsync(successor, intent, ct);
        _notice = "已删除目录条目及其队列项；刷新来源可重新导入";
    }, token);

    private async Task AfterCurrentRemovedAsync(int index, PlaybackIntent intent, CancellationToken ct)
    {
        _recoveryPending = false; _retryAttempt = 0; _streamFailures = 0;
        await StopPlayerAsync(ct);
        _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0, Intent = PlaybackIntent.Stopped };
        // Deleting the last playing item stops even in repeat mode: never wrap unexpectedly on deletion.
        if (index < _state.Queue.Count)
        {
            _state.Playback = _state.Playback with { CurrentId = _state.Queue[index].Id };
            if (intent != PlaybackIntent.Stopped) await StartAsync(index, 0, intent == PlaybackIntent.Paused, ct);
        }
    }

    public Task PlayAsync(Guid? id = null, CancellationToken token = default) => UserActionAsync(async ct =>
    {
        if (id is not null) DetachPreparedQueues();
        if (id is null && _preparedQueue is { } waiting && (waiting.Waiting || !waiting.Started && _activePlayback is null &&
            (_state.Playback.CurrentId is null || waiting.Autoplay)))
        {
            waiting.Autoplay = true; waiting.Paused = false;
            _state.Playback = _state.Playback with { Intent = PlaybackIntent.Playing };
            await FeedPreparedQueueAsync(ct);
            return;
        }
        if (id is null) foreach (var preparation in _preparedQueues) preparation.Paused = false;
        if (id is not null) await SwitchToManualAsync(ct);
        if (_state.ActiveSource == PlaybackSource.Roaming && (CurrentIndex() < 0 || _state.Playback.Intent == PlaybackIntent.Stopped))
        { await BeginRoamingAsync(false, ct); return; }
        _recoveryPending = false; _retryAttempt = 0; _streamFailures = 0;
        var index = id is { } selected ? _state.Queue.FindIndex(q => q.Id == selected) : CurrentIndex();
        if (index < 0) index = 0;
        if (id is null && _activePlayback is not null)
        {
            await _player.SetPausedAsync(false, ct);
            _state.Playback = _state.Playback with { Intent = PlaybackIntent.Playing };
            _lastAdvance = _time.GetUtcNow();
        }
        else await StartAsync(index, id is null ? _state.Playback.PositionSeconds : 0, false, ct);
    }, token);

    public Task PauseAsync(CancellationToken token = default) => UserActionAsync(async ct =>
    {
        foreach (var preparing in _preparedQueues) preparing.Paused = true;
        if (_state.Playback.Intent == PlaybackIntent.Stopped && _preparedQueue is not { Started: false, Autoplay: true }) return;
        if (_activePlayback is not null) await _player.SetPausedAsync(true, ct);
        await CapturePositionAsync(ct);
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
        _notice = "已暂停";
    }, token);

    public Task StopAsync(CancellationToken token = default) => UserActionAsync(async ct =>
    {
        foreach (var preparing in _preparedQueues) { preparing.Autoplay = false; preparing.Waiting = false; }
        _recoveryPending = false; _retryAttempt = 0; _streamFailures = 0;
        await StopPlayerAsync(ct);
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
        _notice = "已停止；下次启动不会自动播放";
        if (ActiveRoaming is { } roaming) roaming.RoundActive = false;
    }, token);

    public Task NextAsync(CancellationToken token = default) => NavigateAsync(1, token);
    public Task PreviousAsync(CancellationToken token = default) => NavigateAsync(-1, token);
    private Task NavigateAsync(int direction, CancellationToken token) => UserActionAsync(async ct =>
    {
        DetachPreparedQueues();
        if (_state.ActiveSource == PlaybackSource.Roaming)
        {
            if (ActiveRoaming is { } roaming && _snapshot.Current is { } current)
                roaming.ExcludedBvids.Add(current.Key.Bvid);
            await StartNextRoamingVideoAsync(ct);
            return;
        }
        _recoveryPending = false; _retryAttempt = 0; _streamFailures = 0;
        var index = CurrentIndex();
        var target = index < 0 ? 0 : index + direction;
        if (_state.Playback.Mode == QueueMode.RepeatAll && _state.Queue.Count > 0)
            target = (target + _state.Queue.Count) % _state.Queue.Count;
        if (target < 0) target = 0;
        await StartAsync(target, 0, false, ct);
    }, token);

    public Task SetModeAsync(QueueMode mode, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        if (_state.ActiveSource == PlaybackSource.Manual) _state.Playback = _state.Playback with { Mode = mode };
        else _state.ManualPlayback = _state.ManualPlayback with { Mode = mode };
        return Task.CompletedTask;
    }, token);

    public Task SetVolumeAsync(double volume, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        if (_activePlayback is not null) await _player.SetVolumeAsync(volume, ct);
        _state.Playback = _state.Playback with { Volume = volume };
    }, token);

    public Task SetVideoFitAsync(bool fill, CancellationToken token = default) =>
        ExecuteAsync(ct => _player.SetVideoFitAsync(fill, ct), token);

    /// <summary>Stores the quality preference and, while an item is playing or paused, switches its stream
    /// in place. Stopped sessions only persist the preference; nothing starts playing.</summary>
    public Task SetMediaSelectionAsync(MediaSelectionPreferences selection, CancellationToken token = default) =>
        ExecuteAsync(async ct =>
        {
            var previous = _selection;
            _selection = selection;
            var index = CurrentIndex();
            if (index < 0 || _state.Playback.Intent == PlaybackIntent.Stopped)
            {
                _notice = "已保存画质与音质偏好";
                return;
            }
            await SwitchCurrentMediaAsync(_state.Queue[index], previous, ct);
        }, token);

    /// <summary>Quality switch: resolve the new stream first, then capture the position and replace playback.
    /// The queue is never advanced and no completion is recorded; a failed resolution keeps the old playback.
    /// A failed load attempts the previous selection once; if that also fails the item is paused in place.</summary>
    private async Task SwitchCurrentMediaAsync(QueueItem item, MediaSelectionPreferences previous, CancellationToken ct)
    {
        var media = await _content.ResolveMediaAsync(item.Key, _selection, ct);
        // Keep an existing retry scheduled until resolving the replacement succeeds.
        _recoveryPending = false; _retryAttempt = 0;
        await CapturePositionAsync(ct);
        var paused = _state.Playback.Intent == PlaybackIntent.Paused;
        var entry = _state.Entries.First(e => e.Key == item.Key);
        var position = TimeSpan.FromSeconds(Math.Max(0, _state.Playback.PositionSeconds));
        if (entry.Duration > TimeSpan.Zero && position >= entry.Duration) position = TimeSpan.Zero;
        var volume = _state.Playback.Volume;
        var playbackId = Guid.NewGuid();
        await StopPlayerAsync(ct);
        try
        {
            _activePlayback = playbackId;
            await _player.LoadAsync(media, playbackId, position, paused, volume, ct);
            _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
            _media = media.Selection;
            _lastPosition = position.TotalSeconds; _lastAdvance = _time.GetUtcNow();
            _notice = paused ? "已切换画质与音质，保持暂停" : $"已切换画质与音质：{entry.Title}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { await StopPlayerAsync(CancellationToken.None); } catch { }
            throw;
        }
        catch
        {
            // The new stream failed to load. Try the previous selection once; the preference itself is kept.
            try
            {
                var restored = await _content.ResolveMediaAsync(item.Key, previous, ct);
                var restoreId = Guid.NewGuid();
                _activePlayback = restoreId;
                await _player.LoadAsync(restored, restoreId, position, paused, volume, ct);
                _media = restored.Selection;
                _lastPosition = position.TotalSeconds; _lastAdvance = _time.GetUtcNow();
                _notice = "新画质加载失败，已恢复原画质继续播放";
            }
            catch
            {
                try { await StopPlayerAsync(CancellationToken.None); } catch { }
                _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused, PositionSeconds = position.TotalSeconds };
                _notice = "新画质加载失败且无法恢复，已暂停并保留进度";
            }
        }
    }

    public Task SeekAsync(TimeSpan position, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (position < TimeSpan.Zero || (_snapshot.Current is { Duration: var duration } && duration > TimeSpan.Zero && position > duration))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (_activePlayback is not null) await _player.SeekAsync(position, ct);
        else if (_state.Playback.Intent == PlaybackIntent.Stopped) return;
        _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
        _lastPosition = position.TotalSeconds; _lastAdvance = _time.GetUtcNow();
    }, token);

    public Task ReloadAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (CurrentIndex() < 0) return;
        _recoveryPending = false; _retryAttempt = 0; _streamFailures = 0;
        await CapturePositionAsync(ct);
        await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent != PlaybackIntent.Playing, ct);
    }, token);

    public Task CheckpointAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        await CapturePositionAsync(ct);
        if (_activePlayback is not null && _state.Playback.Intent == PlaybackIntent.Playing)
        {
            if (Math.Abs(_lastPosition - _state.Playback.PositionSeconds) > .1)
            {
                _lastPosition = _state.Playback.PositionSeconds; _lastAdvance = _time.GetUtcNow();
            }
            else if (_time.GetUtcNow() - _lastAdvance >= TimeSpan.FromSeconds(45))
                await ScheduleRecoveryAsync("播放缓冲超时", ct);
        }
    }, token);

    /// <summary>Retries only the current stable identity, with capped backoff. A user stop cancels recovery.</summary>
    public Task RetryRecoveryAsync(bool immediate = false, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_recoveryPending && _state.ActiveSource == PlaybackSource.Roaming && _pendingMetadataBvid is not null &&
            _state.Playback.Intent == PlaybackIntent.Playing &&
            !_suspended && (immediate || _time.GetUtcNow() >= _retryAt))
        { await StartNextRoamingVideoAsync(ct); return; }
        if (!_recoveryPending || _suspended || CurrentIndex() < 0 || _state.Playback.Intent == PlaybackIntent.Stopped ||
            (!immediate && _time.GetUtcNow() < _retryAt)) return;
        _recoveryPending = false;
        await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent == PlaybackIntent.Paused, ct);
    }, token);

    /// <summary>System sleep preserves playback intent without leaving an audible process running.</summary>
    public Task SuspendAsync(CancellationToken token = default) => UserActionAsync(async ct =>
    {
        _suspended = true;
        await CapturePositionAsync(ct);
        await StopPlayerAsync(ct);
        await _player.ResetOutputAsync(ct);
        _notice = "系统休眠，队列与恢复进度已保存";
    }, token);

    public Task ResumeAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        _suspended = false;
        if (CurrentIndex() >= 0 && _state.Playback.Intent != PlaybackIntent.Stopped)
            await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent == PlaybackIntent.Paused, ct);
    }, token);

    /// <summary>Recreates an output if its owned HWND was destroyed, retaining the same queue item and intent.</summary>
    public Task RebindOutputAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_suspended || CurrentIndex() < 0 || _state.Playback.Intent == PlaybackIntent.Stopped) return;
        await CapturePositionAsync(ct);
        await StopPlayerAsync(ct);
        await _player.ResetOutputAsync(ct);
        await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent == PlaybackIntent.Paused, ct);
    }, token);

    private async Task ScheduleRecoveryAsync(string reason, CancellationToken ct)
    {
        await StopPlayerAsync(ct);
        _recoveryPending = true;
        _retryAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(_retryAttempt++, 5))));
        _notice = $"{reason}；保留当前项与进度，等待重新连接（第 {_retryAttempt} 次）";
    }

    private async Task CapturePositionAsync(CancellationToken ct)
    {
        if (_activePlayback is not null && await _player.GetPositionAsync(ct) is { } position && position >= TimeSpan.Zero)
            _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
    }

    private async Task StartAsync(int index, double seconds, bool paused, CancellationToken ct)
    {
        _recoveryPending = false;
        await StopPlayerAsync(ct);
        var attempted = new HashSet<Guid>();
        var first = true;
        while (index >= 0 && index < _state.Queue.Count && attempted.Add(_state.Queue[index].Id))
        {
            var item = _state.Queue[index];
            if (_state.Playback.CurrentId != item.Id) _streamFailures = 0;
            var entry = _state.Entries.First(e => e.Key == item.Key);
            if (first || entry.Availability != EntryAvailability.Failed)
            {
                _state.Playback = _state.Playback with { CurrentId = item.Id, PositionSeconds = seconds,
                    Intent = paused ? PlaybackIntent.Paused : PlaybackIntent.Playing };
                ActivatePreparedQueue(item.Id, paused);
                var playbackId = Guid.NewGuid();
                try
                {
                    if (_suspended) return;
                    _activePlayback = playbackId;
                    var media = await _content.ResolveMediaAsync(item.Key, _selection, ct);
                    // Checkpoint intent before starting: an interrupted load is safe to retry after restart.
                    await _store.SaveAsync(_state, ct);
                    var position = TimeSpan.FromSeconds(Math.Max(0, seconds));
                    if (entry.Duration > TimeSpan.Zero && position >= entry.Duration) position = TimeSpan.Zero;
                    await _player.LoadAsync(media, playbackId, position, paused, _state.Playback.Volume, ct);
                    _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
                    _media = media.Selection;
                    SetAvailability(item.Key, EntryAvailability.Available, null);
                    _retryAttempt = 0; _lastPosition = position.TotalSeconds; _lastAdvance = _time.GetUtcNow();
                    _notice = $"{(paused ? "已恢复并暂停" : "正在播放")}：{entry.Title}";
                    return;
                }
                catch (ContentAccessException ex)
                {
                    if (ex.Kind is ContentFailureKind.Network or ContentFailureKind.RateLimited)
                    {
                        await ScheduleRecoveryAsync("网络暂不可用", ct);
                        return;
                    }
                    if (ex.Kind == ContentFailureKind.AuthenticationRequired)
                    {
                        // Sign-in or entitlement is required: pause this item and keep its position.
                        // The entry is not marked invalid and the queue is not advanced further.
                        await StopPlayerAsync(ct);
                        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                        _notice = "该内容需要登录或相应权益；已暂停并保留进度，登录后可继续";
                        return;
                    }
                    SetAvailability(item.Key, EntryAvailability.Failed, ex.Kind.ToString());
                    await StopPlayerAsync(ct);
                    if (await TryScheduledBoundaryAsync(item.Id, index + 1, ct)) return;
                }
                catch (PlaybackInterruptedException)
                {
                    await ScheduleRecoveryAsync("播放器连接暂不可用", ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Exit/suspend during a load must retain the intent checkpointed before that load.
                    try { await StopPlayerAsync(CancellationToken.None); } catch { }
                    throw;
                }
                catch
                {
                    try { await StopPlayerAsync(CancellationToken.None); } catch { }
                    _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                    throw;
                }
            }
            if (await TryScheduledBoundaryAsync(item.Id, index + 1, ct)) return;
            if (await WaitForPreparedContentAsync(index + 1, ct)) return;
            first = false; seconds = 0; index++;
            if (index == _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll) index = 0;
        }
        await StopPlayerAsync(ct);
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
        _notice = "队列已结束，或剩余条目均不可播放；可刷新来源或手动重试";
    }

    private void Player_Ended(object? sender, QueuePlaybackEnded args) => _ = HandleEndAsync(args);
    private async Task HandleEndAsync(QueuePlaybackEnded args)
    {
        try
        {
            await ExecuteAsync(async ct =>
            {
                if (args.PlaybackId != _activePlayback) return;
                var index = CurrentIndex();
                if (index < 0) return;
                var item = _state.Queue[index];
                _activePlayback = null;
                if (args.Reason == "eof")
                {
                    _streamFailures = 0;
                    _state.Queue[index] = item with { CompletedCount = item.CompletedCount + 1 };
                }
                else if (args.Reason == "disconnect" || (args.Reason == "error" && _streamFailures++ < 2))
                {
                    await ScheduleRecoveryAsync("播放连接中断", ct);
                    return;
                }
                else if (args.Reason == "error") SetAvailability(item.Key, EntryAvailability.Failed, "MediaFailed");
                else
                {
                    _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                    _notice = "播放器已停止；进度已保留，点击播放可恢复";
                    return;
                }
                var next = index + 1;
                if (await TryScheduledBoundaryAsync(item.Id, next, ct)) return;
                if (_state.ActiveSource == PlaybackSource.Roaming && next >= _state.Queue.Count)
                {
                    CompleteRoamingVideo(item.Key.Bvid);
                    await StartNextRoamingVideoAsync(ct);
                    return;
                }
                if (await WaitForPreparedContentAsync(next, ct)) return;
                if (next == _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll) next = 0;
                // Automatic advancement does not retry a previously failed first candidate.
                var visited = 0;
                while (next < _state.Queue.Count && _state.Entries.First(e => e.Key == _state.Queue[next].Key).Availability == EntryAvailability.Failed)
                {
                    if (await TryScheduledBoundaryAsync(_state.Queue[next].Id, next + 1, ct)) return;
                    if (++visited >= _state.Queue.Count) { next = _state.Queue.Count; break; }
                    next++;
                    if (next == _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll) next = 0;
                }
                await StartAsync(next, 0, false, ct);
                if (_state.ActiveSource == PlaybackSource.Roaming && _state.Playback.Intent == PlaybackIntent.Stopped)
                {
                    CompleteRoamingVideo(item.Key.Bvid);
                    await StartNextRoamingVideoAsync(ct);
                }
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch { /* ExecuteAsync publishes a safe error and preserves the checkpoint. */ }
    }

    private void SetAvailability(ContentKey key, EntryAvailability availability, string? failure)
    {
        var index = _state.Entries.FindIndex(e => e.Key == key);
        _state.Entries[index] = _state.Entries[index] with { Availability = availability, Failure = failure };
    }

    private async Task StopPlayerAsync(CancellationToken ct)
    {
        _activePlayback = null; // Invalidate old callbacks before an intentional stop/replacement.
        await _player.StopAsync(ct);
    }
    private int CurrentIndex() => _state.Queue.FindIndex(q => q.Id == _state.Playback.CurrentId);
    private static void Move<T>(List<T> items, int index, int offset)
    {
        if (index < 0 || items.Count == 0) return;
        var target = Math.Clamp(index + offset, 0, items.Count - 1);
        var item = items[index]; items.RemoveAt(index); items.Insert(target, item);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token, bool initializing = false)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized && !initializing) throw new InvalidOperationException("Initialize the library before use.");
            await action(token);
            SaveActiveContext();
            await _store.SaveAsync(_state, token);
        }
        catch
        {
            _notice = "操作失败；请检查网络、来源或播放器配置后重试";
            if (_initialized)
                try { SaveActiveContext(); await _store.SaveAsync(_state, CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            Volatile.Write(ref _snapshot, new(_state.Entries.ToArray(), _state.Sources.ToArray(),
                _state.Queue.ToArray(), _state.Playback, _notice, _recoveryPending, _recoveryPending ? _retryAt : null,
                _activePlayback is not null && !_recoveryPending, _media, _state.ActiveSource, CreateRoamingSnapshot(),
                ManualItems.ToArray(), _state.ActiveSource == PlaybackSource.Manual ? _state.Playback : _state.ManualPlayback,
                new(_state.Agent.Playlists.ToArray(), _state.Agent.Tasks.ToArray(), _state.Agent.QueueRevision,
                    _preparedQueue is { Waiting: true })));
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _player.Ended -= Player_Ended;
            try
            {
                if (_initialized)
                {
                    await CapturePositionAsync(CancellationToken.None);
                    SaveActiveContext();
                    await _store.SaveAsync(_state, CancellationToken.None);
                }
            }
            finally { await _player.DisposeAsync(); }
        }
        finally { _gate.Release(); }
    }
}
