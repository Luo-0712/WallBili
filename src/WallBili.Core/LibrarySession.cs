namespace WallBili.Core;

/// <summary>Serializes queue edits, EOF callbacks and checkpoints. A failed traversal visits each item at most once.</summary>
public sealed class LibrarySession : IAsyncDisposable
{
    private readonly IContentClient _content;
    private readonly ILibraryStore _store;
    private readonly IQueuePlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private LibraryState _state = new();
    private Guid? _activePlayback;
    private bool _disposed;
    private bool _initialized;
    private string _notice = "就绪";
    private LibrarySnapshot _snapshot = new([], [], [], new(), "就绪");

    public LibrarySession(IContentClient content, ILibraryStore store, IQueuePlayer player)
    {
        _content = content; _store = store; _player = player;
        _player.Ended += Player_Ended;
    }

    public LibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsInitialized => _initialized;
    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_initialized) throw new InvalidOperationException("The library is already initialized.");
        _state = await _store.LoadAsync(ct);
        _initialized = true;
        if (_state.Playback.CurrentId is { } id && !_state.Queue.Any(q => q.Id == id))
            _state.Playback = _state.Playback with { CurrentId = null, Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
        _notice = "已恢复本地目录与队列";
        if (_state.Playback.CurrentId is not null && _state.Playback.Intent != PlaybackIntent.Stopped)
            await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent == PlaybackIntent.Paused, ct);
    }, token, initializing: true);

    public async Task ImportVideoAsync(string input, CancellationToken token = default) =>
        await ApplyImportAsync(await new CatalogImporter(_content).VideoAsync(input, token), token);
    public async Task ImportCollectionAsync(CollectionSource source, CancellationToken token = default) =>
        await ApplyImportAsync(await new CatalogImporter(_content).CollectionAsync(source, token), token);

    public async Task RefreshSourceAsync(string id, CancellationToken token = default)
    {
        var source = Snapshot.Sources.First(s => s.Id == id);
        if (source.Collection is { } collection) await ImportCollectionAsync(collection, token);
        else await ImportVideoAsync(source.VideoInput!, token);
    }

    private Task ApplyImportAsync(CatalogImport import, CancellationToken token) => ExecuteAsync(ct =>
    {
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
        _state.Sources.RemoveAll(s => s.Id == id);
        _notice = "已移除来源，目录与队列保留";
        return Task.CompletedTask;
    }, token);

    public Task EnqueueAsync(IEnumerable<ContentKey> keys, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        var selected = keys.ToArray();
        if (selected.Any(key => !_state.Entries.Any(e => e.Key == key))) throw new ArgumentException("Content is not in the catalog.");
        foreach (var key in selected)
        {
            _state.Queue.Add(new(Guid.NewGuid(), key));
        }
        _notice = "已加入队列";
        return Task.CompletedTask;
    }, token);

    public Task MoveQueueAsync(Guid id, int offset, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        Move(_state.Queue, _state.Queue.FindIndex(q => q.Id == id), offset);
        return Task.CompletedTask;
    }, token);

    public Task MoveEntryAsync(ContentKey key, int offset, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        Move(_state.Entries, _state.Entries.FindIndex(e => e.Key == key), offset);
        return Task.CompletedTask;
    }, token);

    public Task RemoveQueueAsync(Guid id, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        var index = _state.Queue.FindIndex(q => q.Id == id);
        if (index < 0) return;
        var current = _state.Playback.CurrentId == id;
        var intent = _state.Playback.Intent;
        _state.Queue.RemoveAt(index);
        if (current) await AfterCurrentRemovedAsync(index, intent, ct);
        _notice = "已从队列移除";
    }, token);

    public Task RemoveEntryAsync(ContentKey key, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        var current = CurrentIndex();
        var removesCurrent = current >= 0 && _state.Queue[current].Key == key;
        var successor = current < 0 ? 0 : _state.Queue.Take(current).Count(q => q.Key != key);
        var intent = _state.Playback.Intent;
        _state.Queue.RemoveAll(q => q.Key == key);
        _state.Entries.RemoveAll(e => e.Key == key);
        for (var i = 0; i < _state.Sources.Count; i++)
            _state.Sources[i] = _state.Sources[i] with { Members = _state.Sources[i].Members.Where(k => k != key).ToArray() };
        if (removesCurrent) await AfterCurrentRemovedAsync(successor, intent, ct);
        _notice = "已删除目录条目及其队列项；刷新来源可重新导入";
    }, token);

    private async Task AfterCurrentRemovedAsync(int index, PlaybackIntent intent, CancellationToken ct)
    {
        await StopPlayerAsync(ct);
        _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0, Intent = PlaybackIntent.Stopped };
        // Deleting the last playing item stops even in repeat mode: never wrap unexpectedly on deletion.
        if (index < _state.Queue.Count)
        {
            _state.Playback = _state.Playback with { CurrentId = _state.Queue[index].Id };
            if (intent != PlaybackIntent.Stopped) await StartAsync(index, 0, intent == PlaybackIntent.Paused, ct);
        }
    }

    public Task PlayAsync(Guid? id = null, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        var index = id is { } selected ? _state.Queue.FindIndex(q => q.Id == selected) : CurrentIndex();
        if (index < 0) index = 0;
        if (id is null && _activePlayback is not null)
        {
            await _player.SetPausedAsync(false, ct);
            _state.Playback = _state.Playback with { Intent = PlaybackIntent.Playing };
        }
        else await StartAsync(index, id is null ? _state.Playback.PositionSeconds : 0, false, ct);
    }, token);

    public Task PauseAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (_activePlayback is null) return;
        await _player.SetPausedAsync(true, ct);
        await CapturePositionAsync(ct);
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
        _notice = "已暂停";
    }, token);

    public Task StopAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        await StopPlayerAsync(ct);
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Stopped, PositionSeconds = 0 };
        _notice = "已停止；下次启动不会自动播放";
    }, token);

    public Task NextAsync(CancellationToken token = default) => NavigateAsync(1, token);
    public Task PreviousAsync(CancellationToken token = default) => NavigateAsync(-1, token);
    private Task NavigateAsync(int direction, CancellationToken token) => ExecuteAsync(async ct =>
    {
        var index = CurrentIndex();
        var target = index < 0 ? 0 : index + direction;
        if (_state.Playback.Mode == QueueMode.RepeatAll && _state.Queue.Count > 0)
            target = (target + _state.Queue.Count) % _state.Queue.Count;
        if (target < 0) target = 0;
        await StartAsync(target, 0, false, ct);
    }, token);

    public Task SetModeAsync(QueueMode mode, CancellationToken token = default) => ExecuteAsync(ct =>
    {
        _state.Playback = _state.Playback with { Mode = mode };
        return Task.CompletedTask;
    }, token);

    public Task SetVolumeAsync(double volume, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        if (_activePlayback is not null) await _player.SetVolumeAsync(volume, ct);
        _state.Playback = _state.Playback with { Volume = volume };
    }, token);

    public Task SeekAsync(TimeSpan position, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (position < TimeSpan.Zero || (_snapshot.Current is { Duration: var duration } && duration > TimeSpan.Zero && position > duration))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (_activePlayback is null) return;
        await _player.SeekAsync(position, ct);
        _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
    }, token);

    public Task ReloadAsync(CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        if (CurrentIndex() < 0) return;
        await CapturePositionAsync(ct);
        await StartAsync(CurrentIndex(), _state.Playback.PositionSeconds, _state.Playback.Intent != PlaybackIntent.Playing, ct);
    }, token);

    public Task CheckpointAsync(CancellationToken token = default) => ExecuteAsync(CapturePositionAsync, token);

    private async Task CapturePositionAsync(CancellationToken ct)
    {
        if (_activePlayback is not null && await _player.GetPositionAsync(ct) is { } position && position >= TimeSpan.Zero)
            _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
    }

    private async Task StartAsync(int index, double seconds, bool paused, CancellationToken ct)
    {
        await StopPlayerAsync(ct);
        var attempted = new HashSet<Guid>();
        var first = true;
        while (index >= 0 && index < _state.Queue.Count && attempted.Add(_state.Queue[index].Id))
        {
            var item = _state.Queue[index];
            var entry = _state.Entries.First(e => e.Key == item.Key);
            if (first || entry.Availability != EntryAvailability.Failed)
            {
                _state.Playback = _state.Playback with { CurrentId = item.Id, PositionSeconds = seconds,
                    Intent = paused ? PlaybackIntent.Paused : PlaybackIntent.Playing };
                var playbackId = Guid.NewGuid();
                _activePlayback = playbackId;
                try
                {
                    var media = await _content.ResolveMediaAsync(item.Key, ct);
                    // Checkpoint intent before starting: an interrupted load is safe to retry after restart.
                    await _store.SaveAsync(_state, ct);
                    var position = TimeSpan.FromSeconds(Math.Max(0, seconds));
                    if (entry.Duration > TimeSpan.Zero && position >= entry.Duration) position = TimeSpan.Zero;
                    await _player.LoadAsync(media, playbackId, position, paused, _state.Playback.Volume, ct);
                    _state.Playback = _state.Playback with { PositionSeconds = position.TotalSeconds };
                    SetAvailability(item.Key, EntryAvailability.Available, null);
                    _notice = $"{(paused ? "已恢复并暂停" : "正在播放")}：{entry.Title}";
                    return;
                }
                catch (ContentAccessException ex)
                {
                    SetAvailability(item.Key, EntryAvailability.Failed, ex.Kind.ToString());
                    await StopPlayerAsync(ct);
                }
                catch
                {
                    try { await StopPlayerAsync(CancellationToken.None); } catch { }
                    _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                    throw;
                }
            }
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
                if (args.Reason == "eof") _state.Queue[index] = item with { CompletedCount = item.CompletedCount + 1 };
                else if (args.Reason == "error") SetAvailability(item.Key, EntryAvailability.Failed, "MediaFailed");
                else
                {
                    _state.Playback = _state.Playback with { Intent = PlaybackIntent.Paused };
                    _notice = "播放器已停止；进度已保留，点击播放可恢复";
                    return;
                }
                var next = index + 1;
                if (next == _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll) next = 0;
                // Automatic advancement does not retry a previously failed first candidate.
                var visited = 0;
                while (next < _state.Queue.Count && _state.Entries.First(e => e.Key == _state.Queue[next].Key).Availability == EntryAvailability.Failed)
                {
                    if (++visited >= _state.Queue.Count) { next = _state.Queue.Count; break; }
                    next++;
                    if (next == _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll) next = 0;
                }
                await StartAsync(next, 0, false, ct);
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
            await _store.SaveAsync(_state, token);
        }
        catch
        {
            _notice = "操作失败；请检查网络、来源或播放器配置后重试";
            if (_initialized)
                try { await _store.SaveAsync(_state, CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            Volatile.Write(ref _snapshot, new(_state.Entries.ToArray(), _state.Sources.ToArray(),
                _state.Queue.ToArray(), _state.Playback, _notice));
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
                    await _store.SaveAsync(_state, CancellationToken.None);
                }
            }
            finally { await _player.DisposeAsync(); }
        }
        finally { _gate.Release(); }
    }
}
