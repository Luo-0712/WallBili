namespace WallBili.Core;

public sealed partial class LibrarySession
{
    private PreparedQueue? _preparedQueue;
    private readonly List<PreparedQueue> _preparedQueues = [];
    private sealed class PreparedQueue(Guid playlistId, bool autoplay, bool replace, long revision, int startIndex)
    {
        public Guid PlaylistId = playlistId;
        public bool Autoplay = autoplay, Replace = replace, Started, Waiting, Paused;
        public Guid? PendingStart;
        public long Revision = revision;
        public int StartIndex = startIndex;
        public HashSet<ContentKey> Delivered = [];
        public HashSet<Guid> Items = [], Predecessors = [];
    }

    internal Task MutateAgentAsync(Action<AgentLibraryState> action, CancellationToken ct = default) =>
        ExecuteAsync(async token => { action(_state.Agent); await FeedPreparedQueueAsync(token); }, ct);
    internal CommandReceipt? Receipt(string requestId) => _state.Agent.Receipts.GetValueOrDefault(requestId);

    public Task LoadPlaylistAsync(Guid playlistId, string mode = "append", bool autoplay = false,
        int startIndex = 0, long? expectedRevision = null, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        RequirePlaylist(playlistId);
        if (mode is not ("append" or "replace") || startIndex < 0) throw new ArgumentException("Invalid playlist load options.");
        if (mode == "replace") CheckQueueRevision(expectedRevision);
        if (autoplay) { RegisterRoamingIntent(); _pendingRoaming = null; }
        if (mode == "replace" || autoplay) DetachPreparedQueues();
        var binding = new PreparedQueue(playlistId, autoplay, mode == "replace", _state.Agent.QueueRevision, startIndex)
        { Predecessors = ManualItems.Select(q => q.Id).ToHashSet() };
        _preparedQueues.Add(binding);
        _preparedQueue ??= binding;
        await FeedPreparedQueueAsync(ct);
    }, token);

    public Task EditAgentQueueAsync(string action, ContentKey[]? keys = null, Guid[]? itemIds = null,
        int? index = null, long? expectedRevision = null, CancellationToken token = default) => ExecuteAsync(async ct =>
    {
        keys ??= []; itemIds ??= [];
        if (action is not ("append" or "insert_next" or "remove" or "move" or "reorder" or "clear" or "replace"))
            throw new ArgumentException("Unknown queue action.");
        if (action is "replace" or "clear" or "move" or "reorder" or "remove") CheckQueueRevision(expectedRevision);
        if (keys.Any(k => !_state.Entries.Any(e => e.Key == k))) throw new ArgumentException("Import content before enqueueing it.");
        if (itemIds.Distinct().Count() != itemIds.Length || itemIds.Any(id => ManualItems.All(q => q.Id != id)))
            throw new ArgumentException("Unknown or duplicate queue item identity.");
        if (action == "reorder" && itemIds.Length != ManualItems.Count ||
            action == "move" && (itemIds.Length != 1 || index is null || index < 0 || index >= ManualItems.Count))
            throw new ArgumentException("Provide a complete reorder, or one item and a valid move index.");
        if (action is "replace" or "clear") DetachPreparedQueues();
        if (action is "remove" or "clear" or "replace")
        {
            RegisterRoamingIntent(); _pendingRoaming = null;
            var removes = action is "clear" or "replace" ? ManualItems.Select(q => q.Id).ToHashSet() : itemIds.ToHashSet();
            var oldIndex = CurrentIndex();
            var removedCurrent = _state.ActiveSource == PlaybackSource.Manual && _state.Playback.CurrentId is { } current && removes.Contains(current);
            var successor = oldIndex < 0 ? 0 : _state.Queue.Take(oldIndex).Count(q => !removes.Contains(q.Id));
            var intent = _state.Playback.Intent;
            ManualItems.RemoveAll(q => removes.Contains(q.Id));
            if (_state.ActiveSource == PlaybackSource.Roaming)
                _state.ManualPlayback = _state.ManualPlayback with
                { CurrentId = ManualItems.Any(q => q.Id == _state.ManualPlayback.CurrentId) ? _state.ManualPlayback.CurrentId : null };
            if (removedCurrent) await AfterCurrentRemovedAsync(successor, intent, ct);
        }
        if (action is "append" or "insert_next" or "replace")
        {
            var insert = action == "insert_next" ? ManualItems.FindIndex(q => q.Id ==
                (_state.ActiveSource == PlaybackSource.Manual ? _state.Playback.CurrentId : _state.ManualPlayback.CurrentId)) + 1 : ManualItems.Count;
            ManualItems.InsertRange(insert, keys.Select(k => new QueueItem(Guid.NewGuid(), k)));
        }
        if (action == "move")
        {
            var item = ManualItems.First(q => q.Id == itemIds[0]);
            ManualItems.Remove(item); ManualItems.Insert(index!.Value, item);
        }
        if (action == "reorder")
        {
            var reordered = itemIds.Select(id => ManualItems.Single(q => q.Id == id)).ToArray();
            ManualItems.Clear(); ManualItems.AddRange(reordered);
        }
    }, token);

    private void CheckQueueRevision(long? expected)
    {
        if (expected is null || expected != _state.Agent.QueueRevision)
            throw new AgentControlException("Conflict", "Read get_queue and provide its current expectedRevision.");
    }
    private LocalPlaylist RequirePlaylist(Guid id) => _state.Agent.Playlists.FirstOrDefault(p => p.Id == id)
        ?? throw new AgentControlException("NotFound", "The playlist does not exist.");

    internal Task CommitPreparedVideoAsync(Guid taskId, VideoInfo video, ContentRef reference,
        CancellationToken token, Func<bool>? isCurrentSession = null) => ExecuteAsync(async ct =>
    {
        if (isCurrentSession is not null && !isCurrentSession())
            throw new AgentControlException("SessionChanged", "The account changed before content could be committed.");
        var task = _state.Agent.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null || task.State != "running") return;
        var parts = video.Parts.Where(p => reference.Kind != "part" || p.Key.Cid == reference.Cid).OrderBy(p => p.Page).ToArray();
        if (parts.Length == 0) throw new ContentAccessException(ContentFailureKind.InvalidInput, "The selected part does not exist.");
        foreach (var part in parts)
        {
            var entry = new CatalogEntry(part.Key, video.Parts.Count == 1 ? video.Title : video.Title + " · " + part.Title,
                video.Uploader, part.Page, part.Duration);
            var at = _state.Entries.FindIndex(e => e.Key == part.Key);
            if (at < 0) _state.Entries.Add(entry);
            else _state.Entries[at] = entry with { Availability = _state.Entries[at].Availability == EntryAvailability.Available
                ? EntryAvailability.Available : EntryAvailability.Unknown };
        }
        if (task.PlaylistId is { } id && _state.Agent.Playlists.FindIndex(p => p.Id == id) is var pi && pi >= 0)
        {
            var playlist = _state.Agent.Playlists[pi];
            if (!(playlist.ExcludedReferences ?? []).Contains(ReferenceIdentity(reference)))
            {
                var members = playlist.Members.ToList();
                var memberIndex = members.FindIndex(m => ReferenceIdentity(m.Reference) == ReferenceIdentity(reference));
                var keys = parts.Select(p => p.Key).Where(k => !playlist.Excluded.Contains(k)).ToArray();
                var member = new PlaylistMember(memberIndex < 0 ? Guid.NewGuid() : members[memberIndex].Id,
                    reference, video.Title, keys);
                if (memberIndex < 0) members.Add(member); else members[memberIndex] = member;
                _state.Agent.Playlists[pi] = playlist with { Members = members.ToArray(), Revision = playlist.Revision + 1, UpdatedAt = _time.GetUtcNow() };
            }
        }
        await FeedPreparedQueueAsync(ct);
    }, token);

    internal Task MarkPreparationProblemAsync(Guid taskId, ContentRef reference, string failure, CancellationToken ct) =>
        MutateAgentAsync(state =>
        {
            var task = state.Tasks.First(t => t.Id == taskId);
            if (task.PlaylistId is not { } id) return;
            var pi = state.Playlists.FindIndex(p => p.Id == id);
            if (pi < 0) return;
            var p = state.Playlists[pi];
            if ((p.ExcludedReferences ?? []).Contains(ReferenceIdentity(reference))) return;
            var members = p.Members.ToList();
            var i = members.FindIndex(m => ReferenceIdentity(m.Reference) == ReferenceIdentity(reference));
            var member = i < 0 ? new PlaylistMember(Guid.NewGuid(), reference, reference.Bvid ?? reference.Kind, [], failure)
                : members[i] with { Failure = failure };
            if (i < 0) members.Add(member); else members[i] = member;
            state.Playlists[pi] = p with { Members = members.ToArray(), Revision = p.Revision + 1, UpdatedAt = _time.GetUtcNow() };
        }, ct);

    internal Task UpdatePreparationAsync(Guid id, string state, int processed, int? total, string? failure,
        CancellationToken token) => ExecuteAsync(async ct =>
    {
        var i = _state.Agent.Tasks.FindIndex(t => t.Id == id);
        if (i < 0) return;
        _state.Agent.Tasks[i] = _state.Agent.Tasks[i] with { State = state, Processed = processed, Total = total, Failure = failure };
        await FeedPreparedQueueAsync(ct);
    }, token);

    private bool PlaylistPreparing(Guid id) => _state.Agent.Tasks.Any(t => t.PlaylistId == id && t.State is "pending" or "running");
    private void DetachPreparedQueues()
    {
        _preparedQueue = null;
        _preparedQueues.Clear();
    }
    private int PreparedInsertionIndex(PreparedQueue binding)
    {
        var own = ManualItems.FindLastIndex(q => binding.Items.Contains(q.Id));
        if (own >= 0) return own + 1;
        var predecessors = binding.Predecessors.Concat(_preparedQueues.TakeWhile(b => b != binding).SelectMany(b => b.Items)).ToHashSet();
        return ManualItems.FindLastIndex(q => predecessors.Contains(q.Id)) + 1;
    }
    private void ActivatePreparedQueue(Guid itemId, bool paused)
    {
        if (_preparedQueues.FirstOrDefault(b => b.Items.Contains(itemId)) is not { } binding) return;
        _preparedQueue = binding;
        binding.Started = true; binding.Waiting = false; binding.PendingStart = null;
        binding.Autoplay = true; binding.Paused = paused;
    }
    private async Task FeedPreparedQueueAsync(CancellationToken ct)
    {
        foreach (var batch in _preparedQueues.ToArray())
        {
            var playlist = _state.Agent.Playlists.FirstOrDefault(p => p.Id == batch.PlaylistId);
            if (playlist is null) continue;
            var keys = playlist.Members.SelectMany(m => m.Parts).Distinct()
                .Where(k => _state.Entries.Any(e => e.Key == k)).Skip(batch.StartIndex).ToArray();
            var fresh = keys.Where(k => !batch.Delivered.Contains(k)).ToArray();
            if (fresh.Length == 0) continue;
            if (batch.Replace)
            {
                if (_state.Agent.QueueRevision != batch.Revision) { DetachPreparedQueues(); return; }
                if (_state.ActiveSource == PlaybackSource.Manual)
                {
                    await StopPlayerAsync(ct);
                    _state.Playback = _state.Playback with { CurrentId = null, PositionSeconds = 0, Intent = PlaybackIntent.Stopped };
                }
                else _state.ManualPlayback = _state.ManualPlayback with { CurrentId = null, PositionSeconds = 0, Intent = PlaybackIntent.Stopped };
                ManualItems.Clear(); batch.Replace = false; batch.Predecessors.Clear();
            }
            var items = fresh.Select(k => new QueueItem(Guid.NewGuid(), k)).ToArray();
            ManualItems.InsertRange(PreparedInsertionIndex(batch), items);
            foreach (var item in items) { batch.Delivered.Add(item.Key); batch.Items.Add(item.Id); }
            if ((!batch.Started || batch.Waiting) && batch.PendingStart is null) batch.PendingStart = items[0].Id;
        }
        if (_preparedQueue is not { } binding) return;
        if (binding.PendingStart is { } pendingId && ManualItems.All(q => q.Id != pendingId)) binding.PendingStart = null;
        if (binding.Autoplay && !binding.Paused && binding.PendingStart is { } pending && (!binding.Started || binding.Waiting))
        {
            await SwitchToManualAsync(ct);
            binding.Started = true; binding.Waiting = false; binding.PendingStart = null;
            var index = _state.Queue.FindIndex(q => q.Id == pending);
            if (index >= 0) await StartAsync(index, 0, false, ct);
            return;
        }
        if ((!binding.Started || binding.Waiting) && binding.PendingStart is null &&
            !PlaylistPreparing(binding.PlaylistId) && binding.Autoplay && !binding.Paused)
        {
            var next = binding.Started ? CurrentIndex() + 1 : PreparedInsertionIndex(binding);
            if (await WaitForPreparedContentAsync(next, ct)) return;
            binding.Waiting = false;
            if (next >= _state.Queue.Count && _state.Playback.Mode == QueueMode.RepeatAll && _state.Queue.Count > 0) next = 0;
            if (_state.ActiveSource == PlaybackSource.Manual) await StartAsync(next, 0, false, ct);
        }
    }
    private async Task<bool> WaitForPreparedContentAsync(int nextIndex, CancellationToken ct)
    {
        if (_state.ActiveSource != PlaybackSource.Manual || _state.Playback.Intent != PlaybackIntent.Playing) return false;
        var current = _state.Playback.CurrentId;
        var currentBatch = _preparedQueues.FindIndex(b => current is { } id && b.Items.Contains(id));
        var binding = _preparedQueues.Skip(Math.Max(0, currentBatch)).FirstOrDefault(b =>
            PlaylistPreparing(b.PlaylistId) && PreparedInsertionIndex(b) <= nextIndex);
        if (binding is null) return false;
        await StopPlayerAsync(ct);
        _preparedQueue = binding;
        binding.Autoplay = true; binding.Waiting = true;
        _state.Playback = _state.Playback with { Intent = PlaybackIntent.Playing };
        _notice = "等待后续内容";
        return true;
    }
    private void PruneAgentContent(ContentKey key)
    {
        for (var i = 0; i < _state.Agent.Playlists.Count; i++)
        {
            var p = _state.Agent.Playlists[i];
            if (!p.Members.Any(m => m.Parts.Contains(key))) continue;
            _state.Agent.Playlists[i] = p with { Members = p.Members.Select(m => m with { Parts = m.Parts.Where(k => k != key).ToArray() }).ToArray(),
                Excluded = p.Excluded.Append(key).Distinct().ToArray(), Revision = p.Revision + 1 };
        }
    }
    internal static string ReferenceIdentity(ContentRef r) => r.Kind switch
    {
        "video" => "video:" + r.Bvid,
        "part" => $"part:{r.Bvid}:{r.Cid}",
        "collection" => $"{r.CollectionKind}:{r.UploaderId}:{r.CollectionId}",
        "playlist" => "playlist:" + r.PlaylistId,
        _ => throw new ArgumentException("Unsupported content reference.")
    };

    internal Task MergeAgentSourceAsync(CollectionSource? collection, string bvid, string title,
        ContentKey[] keys, CancellationToken token) => ExecuteAsync(ct =>
    {
        var id = collection is null ? "video:" + bvid : $"{collection.Kind}:{collection.UploaderId}:{collection.CollectionId}";
        if (_state.Agent.RemovedSources.Contains(id, StringComparer.Ordinal)) return Task.CompletedTask;
        var i = _state.Sources.FindIndex(s => s.Id == id);
        var old = i < 0 ? null : _state.Sources[i];
        var source = new CatalogSource(id, title, collection is null ? bvid : null, collection,
            _time.GetUtcNow(), (old?.Members ?? []).Concat(keys).Distinct().ToArray(), old?.Problems ?? []);
        if (i < 0) _state.Sources.Add(source); else _state.Sources[i] = source;
        return Task.CompletedTask;
    }, token);
}
