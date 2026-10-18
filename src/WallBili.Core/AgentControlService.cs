using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallBili.Core;

/// <summary>Application-owned jobs and commands, independent of any assistant connection.</summary>
public sealed class AgentControlService(LibrarySession library, IContentClient content,
    IContentSearchClient? search = null, Func<bool, CancellationToken, Task>? setVideoVisible = null) : IWallBiliControl, IAsyncDisposable
{
    private readonly SemaphoreSlim _commands = new(1, 1), _downloads = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancel, Task Work)> _jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _playlistGates = new();
    private bool _disposed;
    public IReadOnlyList<AgentToolDefinition> Tools => AgentToolCatalog.All;

    public async Task<AgentResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var definition = Tools.FirstOrDefault(t => t.Name == name);
        if (definition is null) return AgentResult.Error("NotFound", "Unknown tool.", "List the available tools.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!library.IsInitialized) throw new AgentControlException("NotReady", "WallBili is still initializing.");
            ValidateArguments(definition, arguments);
            if (definition.ReadOnly) return await ReadAsync(name, arguments, cancellationToken);
            await _commands.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var requestId = Text(arguments, "requestId");
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + Canonical(arguments))));
                if (requestId is not null && library.Receipt(requestId) is { } receipt)
                {
                    if (receipt.Fingerprint != fingerprint) throw new AgentControlException("Conflict", "requestId was already used with different arguments.");
                    return JsonSerializer.Deserialize<AgentResult>(receipt.Result, AgentJson.Options)!;
                }
                // Durable reservation precedes any side effect. A crash or ambiguous failure can never
                // cause the same command to run a second time; successful calls replace it with the result.
                if (requestId is not null)
                    await library.MutateAgentAsync(state => state.Receipts[requestId] = new(fingerprint,
                        JsonSerializer.SerializeToElement(AgentResult.Error("Accepted", "This requestId was accepted; its final outcome is not available.",
                            "Read current playback, playlists or tasks. Use a new requestId only for a new action."), AgentJson.Options)), CancellationToken.None);
                AgentResult result;
                try { result = await WriteAsync(name, arguments, CancellationToken.None); }
                finally
                {
                    // Persistence can fail after a mutation was checkpointed. Ensure its accepted tasks
                    // still acquire a worker instead of remaining pending until the next app restart.
                    foreach (var task in library.Snapshot.Agent!.Tasks.Where(t => t.State == "pending"))
                        if (!_jobs.ContainsKey(task.Id)) Schedule(task.Id);
                }
                if (requestId is not null && !result.IsError)
                    await library.MutateAgentAsync(state => state.Receipts[requestId] = new(fingerprint,
                        JsonSerializer.SerializeToElement(result, AgentJson.Options)), CancellationToken.None);
                return result;
            }
            finally { _commands.Release(); }
        }
        catch (AgentControlException e) { return AgentResult.Error(e.Code, e.Message, e.Code == "Conflict" ? "Read current state and use its revision or a new requestId." : "Check current library state and retry."); }
        catch (ContentAccessException e) { return Failure(e.Kind.ToString()); }
        catch (ObjectDisposedException) { return AgentResult.Error("NotReady", "WallBili is shutting down.", "Wait until the application is running again."); }
        catch (Exception e) when (e is ArgumentException or JsonException or FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
        { return AgentResult.Error("InvalidInput", "The supplied arguments are invalid.", "Use the tool schema and references returned by read tools."); }
        catch (OperationCanceledException) { return AgentResult.Error("Cancelled", "The operation was cancelled.", "Query state before retrying a mutation with the same requestId."); }
        catch { return AgentResult.Error("OperationFailed", "The operation could not be completed.", "Check WallBili's player configuration, local storage and current status."); }
    }

    private async Task<AgentResult> ReadAsync(string name, JsonElement a, CancellationToken ct)
    {
        var snapshot = library.Snapshot;
        var agent = snapshot.Agent!;
        switch (name)
        {
            case "get_status": return AgentResult.Success("当前播放状态", new { snapshot.Playback, snapshot.Current,
                snapshot.Source, outputActive = IsPlaying(snapshot), snapshot.RecoveryPending, snapshot.NextRecoveryAt,
                agent.WaitingForContent, agent.QueueRevision, snapshot.Notice,
                tasks = agent.Tasks.Where(t => t.State is "pending" or "running" or "failed" or "interrupted").ToArray() });
            case "search_content":
                if (search is null) return Failure("Unsupported");
                var kind = Text(a, "kind", "video")!;
                var order = Text(a, "order", "relevance")!;
                order = order switch { "published" => "pubdate", "views" => "click", "favorites" => "stow", _ => order };
                return AgentResult.Success("搜索完成", await search.SearchAsync(RequiredText(a, "query"), kind, Page(a), PageSize(a), order, ct));
            case "list_creator_content":
                if (search is null) return Failure("Unsupported");
                return AgentResult.Success("已读取 UP 主内容", await search.GetCreatorContentAsync(Long(a, "uploaderId") ?? 0,
                    Text(a, "kind", "videos")!, Page(a), PageSize(a), ct));
            case "get_content":
                var reference = Reference(a);
                if (reference.Kind == "collection")
                {
                    var page = await content.GetCollectionPageAsync(ToCollection(reference), Page(a), PageSize(a), ct);
                    return AgentResult.Success("已读取合集目录", new { reference, page.Title, page.Page, page.PageSize, page.Total, page.HasMore,
                        items = page.Entries.Select(e => new { reference = new ContentRef("video", e.Bvid), e.Title, e.Duration }).ToArray() });
                }
                if (reference.Kind == "playlist") return PlaylistResult(Playlist(reference.PlaylistId!.Value), a);
                var video = await content.GetVideoAsync(reference.Bvid!, ct);
                if (reference.Kind == "part" && !video.Parts.Any(p => p.Key.Cid == reference.Cid))
                    throw new ArgumentException("The requested part does not exist.");
                return AgentResult.Success("已读取视频分 P", new { reference = new ContentRef("video", video.Bvid), video.Title, video.Uploader,
                    video.UploaderId, video.Duration,
                    parts = video.Parts.Select(p => new { reference = new ContentRef("part", p.Key.Bvid, p.Key.Cid), p.Page, p.Title, p.Duration }).ToArray(),
                    relatedCollections = video.RelatedCollections?.Select(c => new ContentRef("collection", UploaderId: c.UploaderId,
                        CollectionId: c.CollectionId, CollectionKind: c.Kind)).ToArray() });
            case "list_sources": return AgentResult.Success("已读取来源", Paginate(snapshot.Sources, a));
            case "list_playlists": return AgentResult.Success("已读取片单", Paginate(agent.Playlists.Select(p => new
                { p.Id, p.Name, p.Revision, p.TaskId, p.UpdatedAt, memberCount = p.Members.Length, partCount = p.Members.Sum(m => m.Parts.Length) }).ToArray(), a));
            case "get_playlist": return PlaylistResult(Playlist(GuidValue(a, "playlistId")), a);
            case "get_queue":
                var items = snapshot.ManualQueue ?? snapshot.Queue;
                return AgentResult.Success("已读取手动队列", new { revision = agent.QueueRevision, activeSource = snapshot.Source,
                    manualPlayback = snapshot.ManualPlayback ?? snapshot.Playback, queue = Paginate(items.Select(q => new
                    { q.Id, reference = new ContentRef("part", q.Key.Bvid, q.Key.Cid), q.CompletedCount,
                        entry = snapshot.Entries.FirstOrDefault(e => e.Key == q.Key) }).ToArray(), a) });
            case "get_task":
                if (a.TryGetProperty("taskId", out _)) return AgentResult.Success("已读取任务", TaskById(GuidValue(a, "taskId")));
                return AgentResult.Success("已读取任务列表", Paginate(agent.Tasks.Reverse().ToArray(), a));
            default: throw new ArgumentException("Unknown read tool.");
        }
    }

    private async Task<AgentResult> WriteAsync(string name, JsonElement a, CancellationToken ct)
    {
        switch (name)
        {
            case "create_playlist":
            {
                var title = RequiredText(a, "name"); var refs = References(a);
                var id = Guid.NewGuid(); var taskId = Guid.NewGuid();
                await library.MutateAgentAsync(s =>
                {
                    EnsureJobCapacity(s, refs);
                    ClearSourceTombstones(s, refs);
                    s.Playlists.Add(new(id, title, 0, refs, [], [], taskId, DateTimeOffset.UtcNow));
                    s.Tasks.Add(new(taskId, id, refs, refs.Length == 0 ? "completed" : "pending"));
                }, ct);
                if (refs.Length > 0) Schedule(taskId);
                return AgentResult.Success("片单已创建，内容在后台准备", new { playlistId = id, taskId });
            }
            case "edit_playlist":
            {
                var id = GuidValue(a, "playlistId"); var action = RequiredText(a, "action");
                var refs = action == "append" ? References(a) : [];
                var ids = IdArray(a, "memberIds");
                var newName = action == "rename" ? RequiredText(a, "name") : null;
                if (action == "append" && refs.Length == 0 || action == "remove" && ids.Length == 0)
                    throw new ArgumentException("Provide the members or references to edit.");
                Guid? taskId = refs.Length > 0 ? Guid.NewGuid() : null;
                await library.MutateAgentAsync(s =>
                {
                    var i = s.Playlists.FindIndex(p => p.Id == id);
                    if (i < 0) throw new AgentControlException("NotFound", "The playlist does not exist.");
                    var p = s.Playlists[i]; CheckRevision(p, a);
                    EnsureJobCapacity(s, refs);
                    if (action is not ("rename" or "append" or "remove" or "reorder")) throw new ArgumentException("Unknown playlist action.");
                    if (ids.Distinct().Count() != ids.Length || ids.Any(mid => p.Members.All(m => m.Id != mid)) ||
                        action == "reorder" && ids.Length != p.Members.Length) throw new ArgumentException("Invalid member order or identity.");
                    if (action == "rename") p = p with { Name = newName! };
                    if (action == "append") p = p with { Inputs = p.Inputs.Concat(refs).Distinct().ToArray(), TaskId = taskId ?? p.TaskId };
                    if (action == "remove") p = p with
                    {
                        Excluded = p.Excluded.Concat(p.Members.Where(m => ids.Contains(m.Id)).SelectMany(m => m.Parts)).Distinct().ToArray(),
                        ExcludedReferences = (p.ExcludedReferences ?? []).Concat(p.Members.Where(m => ids.Contains(m.Id))
                            .Select(m => LibrarySession.ReferenceIdentity(m.Reference))).Distinct().ToArray(),
                        Members = p.Members.Where(m => !ids.Contains(m.Id)).ToArray()
                    };
                    if (action == "reorder") p = p with { Members = ids.Select(mid => p.Members.Single(m => m.Id == mid)).ToArray() };
                    s.Playlists[i] = p with { Revision = p.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
                    if (taskId is { } job) { ClearSourceTombstones(s, refs); s.Tasks.Add(new(job, id, refs, "pending")); }
                }, ct);
                if (taskId is { } jobId) Schedule(jobId);
                return AgentResult.Success("片单已更新", new { playlistId = id, taskId, revision = Playlist(id).Revision });
            }
            case "delete_playlist":
            {
                var id = GuidValue(a, "playlistId");
                await library.MutateAgentAsync(s =>
                {
                    var p = s.Playlists.FirstOrDefault(p => p.Id == id) ?? throw new AgentControlException("NotFound", "Playlist not found.");
                    CheckRevision(p, a); s.Playlists.Remove(p);
                }, ct);
                foreach (var task in library.Snapshot.Agent!.Tasks.Where(t => t.PlaylistId == id)) CancelWorker(task.Id);
                return AgentResult.Success("已删除片单，播放队列保留", new { playlistId = id });
            }
            case "refresh_playlist":
            {
                var p = Playlist(GuidValue(a, "playlistId")); CheckRevision(p, a);
                return await CreateJobAsync(p.Inputs, p.Id, ct, Long(a, "expectedRevision"));
            }
            case "import_source": return await CreateJobAsync([Reference(a)], null, ct);
            case "refresh_source":
            {
                var source = library.Snapshot.Sources.FirstOrDefault(s => s.Id == RequiredText(a, "sourceId"))
                    ?? throw new AgentControlException("NotFound", "Source not found.");
                return await CreateJobAsync([source.Collection is { } c ? new("collection", UploaderId: c.UploaderId,
                    CollectionId: c.CollectionId, CollectionKind: c.Kind) : new("video", source.VideoInput)], null, ct);
            }
            case "remove_source":
                await library.RemoveSourceAsync(RequiredText(a, "sourceId"), ct);
                return AgentResult.Success("已移除来源，片库及队列保留", new { });
            case "load_playlist":
                await library.LoadPlaylistAsync(GuidValue(a, "playlistId"), Text(a, "mode", "append")!,
                    Bool(a, "autoplay"), Int(a, "startIndex", 0), Long(a, "expectedRevision"), ct);
                return AgentResult.Success("片单已装载；实际播放请查询 get_status", new { playlistId = GuidValue(a, "playlistId"), status = library.Snapshot.Playback,
                    outputActive = IsPlaying(library.Snapshot), revision = library.Snapshot.Agent!.QueueRevision });
            case "edit_queue":
            {
                var action = RequiredText(a, "action");
                var keys = References(a).SelectMany(r => ResolveCatalogKeys(r)).ToArray();
                var ids = IdArray(a, "itemIds");
                if (action is "append" or "insert_next" or "replace" && keys.Length == 0 || action == "remove" && ids.Length == 0)
                    throw new ArgumentException("Provide the queue references or item identities to edit.");
                await library.EditAgentQueueAsync(action, keys, ids,
                    a.TryGetProperty("index", out _) ? Int(a, "index", 0) : null, Long(a, "expectedRevision"), ct);
                return AgentResult.Success("队列已更新", new { revision = library.Snapshot.Agent!.QueueRevision });
            }
            case "control_playback":
                await ControlAsync(a, ct);
                return AgentResult.Success("播放指令已执行；以 outputActive 和 intent 判断实际状态", new { library.Snapshot.Playback,
                    outputActive = IsPlaying(library.Snapshot), library.Snapshot.Agent!.WaitingForContent });
            case "cancel_task":
            {
                var task = TaskById(GuidValue(a, "taskId")); CancelWorker(task.Id);
                if (task.State is "pending" or "running") await library.UpdatePreparationAsync(task.Id, "cancelled", task.Processed, task.Total, "Cancelled", ct);
                return AgentResult.Success("任务已取消，已准备内容保留", new { taskId = task.Id });
            }
            case "retry_task":
            {
                var task = TaskById(GuidValue(a, "taskId"));
                if (task.PlaylistId is { } pid) Playlist(pid);
                if (task.State is not ("pending" or "running" or "completed"))
                {
                    if (_jobs.TryGetValue(task.Id, out var old)) { await old.Work; old.Cancel.Dispose(); _jobs.TryRemove(task.Id, out _); }
                    await library.MutateAgentAsync(state =>
                    {
                        EnsureJobCapacity(state, task.Inputs);
                        var index = state.Tasks.FindIndex(t => t.Id == task.Id);
                        state.Tasks[index] = state.Tasks[index] with { State = "pending", Processed = 0, Total = null, Failure = null };
                    }, ct);
                    Schedule(task.Id);
                }
                return AgentResult.Success("任务可查询进度", new { taskId = task.Id });
            }
            default: throw new ArgumentException("Unknown write tool.");
        }
    }

    private async Task ControlAsync(JsonElement a, CancellationToken ct)
    {
        switch (RequiredText(a, "action"))
        {
            case "play":
                Guid? itemId = a.TryGetProperty("itemId", out _) ? GuidValue(a, "itemId") : null;
                if (itemId is { } id && !(library.Snapshot.ManualQueue ?? library.Snapshot.Queue).Any(q => q.Id == id))
                    throw new ArgumentException("Unknown queue item.");
                await library.PlayAsync(itemId, ct); break;
            case "resume": await library.PlayAsync(token: ct); break;
            case "pause": await library.PauseAsync(ct); break;
            case "stop": await library.StopAsync(ct); break;
            case "next": await library.NextAsync(ct); break;
            case "previous": await library.PreviousAsync(ct); break;
            case "seek": await library.SeekAsync(TimeSpan.FromSeconds(Number(a, "seconds")), ct); break;
            case "set_volume": await library.SetVolumeAsync(Number(a, "volume"), ct); break;
            case "set_mode":
                var mode = RequiredText(a, "mode");
                if (mode is not ("sequential" or "repeatAll")) throw new ArgumentException("Invalid mode.");
                await library.SetModeAsync(mode == "repeatAll" ? QueueMode.RepeatAll : QueueMode.Sequential, ct); break;
            case "show_video": case "hide_video":
                if (setVideoVisible is null) throw new AgentControlException("Unsupported", "Desktop output is not available.");
                await setVideoVisible(RequiredText(a, "action") == "show_video", ct); break;
            default: throw new ArgumentException("Unknown playback action.");
        }
    }

    private async Task<AgentResult> CreateJobAsync(ContentRef[] refs, Guid? playlistId, CancellationToken ct, long? expectedRevision = null)
    {
        var id = Guid.NewGuid();
        await library.MutateAgentAsync(s =>
        {
            EnsureJobCapacity(s, refs);
            if (playlistId is { } pid)
            {
                var index = s.Playlists.FindIndex(p => p.Id == pid);
                if (index < 0) throw new AgentControlException("NotFound", "Playlist not found.");
                if (expectedRevision != s.Playlists[index].Revision) throw new AgentControlException("Conflict", "Playlist revision changed.");
                s.Playlists[index] = s.Playlists[index] with { TaskId = id };
            }
            ClearSourceTombstones(s, refs);
            s.Tasks.Add(new(id, playlistId, refs, refs.Length == 0 ? "completed" : "pending"));
        }, ct);
        if (refs.Length > 0) Schedule(id);
        return AgentResult.Success("后台任务已创建", new { taskId = id, playlistId });
    }
    private void Schedule(Guid id)
    {
        foreach (var pair in _jobs.Where(pair => pair.Value.Work.IsCompleted).ToArray())
            if (_jobs.TryRemove(pair.Key, out var completed)) completed.Cancel.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Registration precedes execution so cancellation can never miss a newly accepted job.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(async () => { await ready.Task; await RunJobAsync(id, source.Token); });
        if (!_jobs.TryAdd(id, (source, work))) { source.Cancel(); source.Dispose(); }
        ready.SetResult();
    }
    private async Task RunJobAsync(Guid id, CancellationToken ct)
    {
        var processed = 0; var partial = false; string? problem = null;
        var versioned = content as IContentSessionVersion;
        var sessionVersion = versioned?.SessionVersion;
        SemaphoreSlim? playlistGate = null; bool slot = false, owner = false;
        try
        {
            var task = TaskById(id);
            if (task.PlaylistId is { } pid)
            { playlistGate = _playlistGates.GetOrAdd(pid, _ => new(1, 1)); await playlistGate.WaitAsync(ct); owner = true; }
            await _downloads.WaitAsync(ct); slot = true;
            await library.UpdatePreparationAsync(id, "running", 0, null, null, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in task.Inputs)
                await ProcessAsync(reference, null, null);
            await library.UpdatePreparationAsync(id, partial ? "partial" : "completed", processed, processed, problem, ct);

            async Task ProcessAsync(ContentRef reference, CollectionSource? collection, string? sourceTitle)
            {
                ct.ThrowIfCancellationRequested();
                CheckSession();
                if (processed >= 10000) throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Preparation limit exceeded.");
                if (reference.Kind == "collection")
                {
                    var native = ToCollection(reference); var seenPages = new HashSet<string>(StringComparer.Ordinal);
                    for (var page = 1; page <= 1000; page++)
                    {
                        var result = await content.GetCollectionPageAsync(native, page, 30, ct);
                        CheckSession();
                        if (result.Page != page || result.PageSize <= 0 || result.HasMore && result.Entries.Count == 0)
                            throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Invalid collection pagination.");
                        var added = 0;
                        foreach (var entry in result.Entries)
                        {
                            if (!seenPages.Add(entry.Bvid)) continue;
                            added++;
                            await ProcessAsync(new("video", entry.Bvid), native, result.Title);
                        }
                        if (!result.HasMore) return;
                        if (added == 0) throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Repeated collection page.");
                    }
                    throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Collection page limit exceeded.");
                }
                if (reference.Kind == "playlist")
                {
                    foreach (var member in Playlist(reference.PlaylistId!.Value).Members) await ProcessAsync(member.Reference, collection, sourceTitle);
                    return;
                }
                if (!seen.Add(LibrarySession.ReferenceIdentity(reference))) return;
                try
                {
                    var video = await content.GetVideoAsync(reference.Bvid!, ct);
                    CheckSession();
                    ct.ThrowIfCancellationRequested();
                    await library.CommitPreparedVideoAsync(id, video, reference, ct, () => versioned?.SessionVersion == sessionVersion);
                    var keys = video.Parts.Where(p => reference.Kind != "part" || p.Key.Cid == reference.Cid).Select(p => p.Key).ToArray();
                    await library.MergeAgentSourceAsync(collection, video.Bvid, sourceTitle ?? video.Title, keys, ct);
                }
                catch (ContentAccessException e) when (e.Kind is ContentFailureKind.Removed or ContentFailureKind.Inaccessible or ContentFailureKind.Unsupported)
                {
                    partial = true; problem = e.Kind.ToString();
                    await library.MarkPreparationProblemAsync(id, reference, problem, ct);
                }
                processed++;
                await library.UpdatePreparationAsync(id, "running", processed, null, problem, ct);
            }
            void CheckSession()
            {
                if (versioned?.SessionVersion != sessionVersion)
                    throw new AgentControlException("SessionChanged", "The account changed during preparation; retry using the current session.");
            }
        }
        catch (OperationCanceledException)
        { await FinalizeAsync(_lifetime.IsCancellationRequested ? "interrupted" : "cancelled", _lifetime.IsCancellationRequested ? "Interrupted" : "Cancelled"); }
        catch (ContentAccessException e) { await FinalizeAsync("failed", e.Kind.ToString()); }
        catch (AgentControlException e) { await FinalizeAsync("failed", e.Code); }
        catch { await FinalizeAsync("failed", "OperationFailed"); }
        finally { if (slot) _downloads.Release(); if (owner) playlistGate!.Release(); }

        async Task FinalizeAsync(string state, string failure)
        {
            try { await library.UpdatePreparationAsync(id, state, processed, null, failure, CancellationToken.None); }
            catch { /* Shutdown still saves the last committed task checkpoint in LibrarySession. */ }
        }
    }

    private void CancelWorker(Guid id) { if (_jobs.TryGetValue(id, out var job)) job.Cancel.Cancel(); }
    private LocalPlaylist Playlist(Guid id) => library.Snapshot.Agent!.Playlists.FirstOrDefault(p => p.Id == id)
        ?? throw new AgentControlException("NotFound", "The playlist does not exist.");
    private PreparationTask TaskById(Guid id) => library.Snapshot.Agent!.Tasks.FirstOrDefault(t => t.Id == id)
        ?? throw new AgentControlException("NotFound", "The preparation task does not exist.");
    private static void CheckRevision(LocalPlaylist p, JsonElement a)
    { if (Long(a, "expectedRevision") != p.Revision) throw new AgentControlException("Conflict", "Read get_playlist and provide its current expectedRevision."); }
    private static AgentResult PlaylistResult(LocalPlaylist p, JsonElement a) => AgentResult.Success("已读取片单", new
        { p.Id, p.Name, p.Revision, p.Inputs, p.TaskId, p.UpdatedAt, members = Paginate(p.Members, a) });
    private ContentKey[] ResolveCatalogKeys(ContentRef reference)
    {
        if (reference.Kind is not ("video" or "part")) throw new ArgumentException("Queue edits require imported video or part references.");
        var keys = library.Snapshot.Entries.Where(e => e.Key.Bvid == reference.Bvid && (reference.Kind != "part" || e.Key.Cid == reference.Cid))
            .OrderBy(e => e.Page).Select(e => e.Key).ToArray();
        if (keys.Length == 0) throw new AgentControlException("NotReady", "Import the content or load a preparing playlist first.");
        return keys;
    }
    private static void ValidateArguments(AgentToolDefinition tool, JsonElement args)
    {
        ValidateSchema(args, tool.InputSchema);
        if (Text(args, "requestId") is { } requestId && string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Empty requestId.");
    }
    private static void ValidateSchema(JsonElement value, JsonElement schema)
    {
        var type = schema.GetProperty("type").GetString();
        if (type == "object")
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected object.");
            var properties = schema.GetProperty("properties");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name) || !properties.TryGetProperty(field.Name, out var fieldSchema)) throw new ArgumentException("Unknown or duplicate property.");
                ValidateSchema(field.Value, fieldSchema);
            }
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                if (!value.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException("Missing argument.");
        }
        else if (type == "array")
        {
            if (value.ValueKind != JsonValueKind.Array || schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new ArgumentException("Invalid array.");
            foreach (var item in value.EnumerateArray()) ValidateSchema(item, schema.GetProperty("items"));
        }
        else if (type == "string")
        {
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Expected text.");
            var text = value.GetString()!;
            if (text.Any(char.IsControl) || schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32() ||
                schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32() ||
                schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(x => x.GetString() == text) ||
                schema.TryGetProperty("format", out _) && (!Guid.TryParse(text, out var id) || id == Guid.Empty)) throw new ArgumentException("Invalid text or identity.");
        }
        else if (type is "integer" or "number")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                type == "integer" && !value.TryGetInt64(out _) || schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() ||
                schema.TryGetProperty("maximum", out var max) && number > max.GetDouble()) throw new ArgumentException("Invalid number.");
        }
        else if (type == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Expected boolean.");
    }

    private static void EnsureJobCapacity(AgentLibraryState state, ContentRef[] refs)
    {
        if (refs.Length > 0 && state.Tasks.Count(t => t.State is "pending" or "running") >= 32)
            throw new AgentControlException("Busy", "At most 32 preparations can be pending; finish or cancel existing tasks first.");
    }
    private static void ClearSourceTombstones(AgentLibraryState state, ContentRef[] refs)
    {
        foreach (var r in refs.Where(r => r.Kind is "video" or "part" or "collection"))
            state.RemovedSources.Remove(r.Kind == "collection" ? $"{r.CollectionKind ?? CollectionKind.Season}:{r.UploaderId}:{r.CollectionId}" : "video:" + r.Bvid);
    }
    private static ContentRef Reference(JsonElement a) => ValidateRef(a.GetProperty("reference").Deserialize<ContentRef>(AgentJson.Options) ?? throw new ArgumentException("Missing reference."));
    private static ContentRef[] References(JsonElement a)
    {
        if (!a.TryGetProperty("references", out var values)) return [];
        var refs = values.Deserialize<ContentRef[]>(AgentJson.Options) ?? throw new ArgumentException("Missing references.");
        if (refs.Length > 200) throw new ArgumentException("At most 200 input references are allowed.");
        return refs.Select(ValidateRef).ToArray();
    }
    private static ContentRef ValidateRef(ContentRef r)
    {
        if (r is null) throw new ArgumentException("Missing reference.");
        if (r.Kind is "video" or "part")
        {
            if (r.Bvid is null || !Regex.IsMatch(r.Bvid, "^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant) || r.Kind == "part" && r.Cid is not > 0)
                throw new ArgumentException("Invalid video reference.");
        }
        else if (r.Kind == "collection")
        { if (r.UploaderId is not > 0 || r.CollectionId is not > 0 || r.CollectionKind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("Invalid collection reference."); }
        else if (r.Kind == "playlist") { if (r.PlaylistId is null || r.PlaylistId == Guid.Empty) throw new ArgumentException("Invalid playlist reference."); }
        else throw new ArgumentException("Unknown reference kind.");
        return r;
    }
    private static CollectionSource ToCollection(ContentRef r) => new(r.UploaderId!.Value, r.CollectionId!.Value, r.CollectionKind ?? CollectionKind.Season);
    private static string? Text(JsonElement a, string name, string? fallback = null) => a.TryGetProperty(name, out var value) ? value.GetString() : fallback;
    private static string RequiredText(JsonElement a, string name) => !string.IsNullOrWhiteSpace(Text(a, name)) ? Text(a, name)!.Trim() : throw new ArgumentException("Missing text.");
    private static long? Long(JsonElement a, string name) => a.TryGetProperty(name, out var value) ? value.GetInt64() : null;
    private static int Int(JsonElement a, string name, int fallback) => a.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    private static bool Bool(JsonElement a, string name) => a.TryGetProperty(name, out var value) && value.GetBoolean();
    private static double Number(JsonElement a, string name) => a.GetProperty(name).TryGetDouble(out var value) && double.IsFinite(value) ? value : throw new ArgumentException("Invalid number.");
    private static Guid GuidValue(JsonElement a, string name) => a.GetProperty(name).GetGuid() is var value && value != Guid.Empty ? value : throw new ArgumentException("Invalid identity.");
    private static Guid[] IdArray(JsonElement a, string name)
    {
        var ids = a.TryGetProperty(name, out var value) ? value.Deserialize<Guid[]>() ?? throw new ArgumentException("Invalid identities.") : [];
        if (ids.Length > 10000 || ids.Any(id => id == Guid.Empty)) throw new ArgumentException("Too many or invalid identities.");
        return ids;
    }
    private static int Page(JsonElement a) => Int(a, "page", 1) is var page && page > 0 ? page : throw new ArgumentException("Invalid page.");
    private static int PageSize(JsonElement a) => Int(a, "pageSize", 20) is var size && size is > 0 and <= 100 ? size : throw new ArgumentException("Page size must be 1..100.");
    private static object Paginate<T>(IReadOnlyList<T> items, JsonElement a)
    { var page = Page(a); var pageSize = PageSize(a); return new { page, pageSize, total = items.Count, hasMore = (long)page * pageSize < items.Count,
        items = items.Skip((int)Math.Min((long)(page - 1) * pageSize, items.Count)).Take(pageSize).ToArray() }; }
    private static string Canonical(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? "{" + string.Join(",", value.EnumerateObject().Where(p => p.Name != "requestId").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}"
        : value.ValueKind == JsonValueKind.Array ? "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]" : value.GetRawText();
    private static AgentResult Failure(string code) => AgentResult.Error(code, "内容访问未完成（" + code + "）。", code switch
    {
        "AuthenticationRequired" => "在 WallBili 登录有权限的账号后重试。",
        "RateLimited" => "暂时停止请求，稍后重试；必要时在 B 站完成账号验证。",
        "Network" => "检查网络后重试。", _ => "检查内容引用、可见性与应用状态后重试。"
    });
    private static bool IsPlaying(LibrarySnapshot snapshot) => snapshot.OutputActive && snapshot.Playback.Intent == PlaybackIntent.Playing;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _commands.WaitAsync();
        try
        {
            await Task.WhenAll(_jobs.Values.Select(j => j.Work));
            foreach (var job in _jobs.Values) job.Cancel.Dispose();
            foreach (var gate in _playlistGates.Values) gate.Dispose();
            _lifetime.Dispose(); _downloads.Dispose();
        }
        finally { _commands.Release(); }
    }
}
