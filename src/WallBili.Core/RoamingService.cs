namespace WallBili.Core;

/// <summary>Account-scoped discovery and clock scheduling. Only LibrarySession controls playback.</summary>
public sealed class RoamingService : IAsyncDisposable
{
    private readonly LibrarySession _library;
    private readonly IContentDiscoveryClient _discovery;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;
    private readonly List<Task> _tasks = [];
    private DateTimeOffset _lastTick, _retryRefreshAt;
    private long? _accountId;
    private bool _accountSet, _authenticationRequired, _initialCheck = true;
    private long _generation;
    private int _ticking;
    public bool IsRefreshing { get; private set; }
    public string Status { get; private set; } = "请先登录并选择参与漫游的 UP 主";
    public event EventHandler? Changed;

    public RoamingService(LibrarySession library, IContentDiscoveryClient discovery, TimeProvider? time = null)
    { _library = library; _discovery = discovery; _time = time ?? TimeProvider.System; _lastTick = _time.GetLocalNow(); }

    private void CancelRefresh()
    {
        lock (_sync)
        {
            _generation++;
            try { _refreshCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            _refreshTask = null; IsRefreshing = false;
        }
    }
    public async Task SetAccountAsync(long? accountId, CancellationToken token = default)
    {
        if (_accountSet && accountId == _accountId) return;
        CancelRefresh(); _accountSet = true; _accountId = accountId; _authenticationRequired = false;
        _initialCheck = true; _retryRefreshAt = default; ResetClock();
        try { await _library.SetRoamingAccountAsync(accountId, token); }
        catch { _accountSet = false; throw; }
        Status = accountId is null ? "请先登录并选择参与漫游的 UP 主" : "订阅设置已读取";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task SaveSubscriptionsAsync(IEnumerable<FollowedCreator> creators, CancellationToken token = default)
    {
        CancelRefresh(); _retryRefreshAt = default;
        await _library.SetSubscriptionsAsync(creators, token);
        Status = "订阅已保存，点击刷新或开始漫游获取投稿";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task RefreshAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _refreshTask = RefreshCoreAsync(_generation, _accountId, _library.RoamingRevision, _refreshCancellation);
                _tasks.RemoveAll(t => t.IsCompleted); _tasks.Add(_refreshTask);
            }
            return _refreshTask.WaitAsync(token);
        }
    }
    private async Task RefreshCoreAsync(long generation, long? accountId, long revision, CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            var ct = cancellation.Token;
            var snapshot = _library.Snapshot.Roaming;
            if (accountId is null || snapshot?.AccountId != accountId) { Status = "请先登录"; return; }
            if (snapshot.Subscriptions.Count == 0) { Status = "请先选择参与漫游的 UP 主"; return; }
            IsRefreshing = true; Status = "正在检查订阅更新…"; _authenticationRequired = false;
            Changed?.Invoke(this, EventArgs.Empty);
            var failed = 0;
            try
            {
                foreach (var creator in snapshot.Subscriptions)
                {
                    ct.ThrowIfCancellationRequested();
                    var sync = snapshot.Sync.GetValueOrDefault(creator.Uid) ?? new();
                    var started = sync.ScanStartedAt ?? _time.GetUtcNow();
                    var seen = new HashSet<string>();
                    try
                    {
                        var finished = false;
                        // Page numbers are not stable cursors: deletions shift later submissions forward.
                        // Retain merged identities and the old watermark, but rescan from page one after failure.
                        for (var page = 1; page <= 1000; page++)
                        {
                            var result = await _discovery.GetCreatorVideosAsync(creator.Uid, page, 30, ct);
                            ct.ThrowIfCancellationRequested();
                            if (generation != Interlocked.Read(ref _generation)) return;
                            if (result.UploaderId != creator.Uid || result.Page != page || result.PageSize <= 0 ||
                                result.HasMore && result.Items.Count == 0 || result.Items.Any(v => v.UploaderId != creator.Uid) ||
                                result.Items.Count > 0 && result.Items.All(v => !seen.Add(v.Bvid)))
                                throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Invalid discovery pagination.");
                            // Add every identity, including the rest of a page after Any/All short-circuits.
                            foreach (var video in result.Items) seen.Add(video.Bvid);
                            finished = !sync.Seeded || !result.HasMore ||
                                sync.Watermark is { } watermark && result.Items.Any(v => v.PublishedAt < watermark);
                            await _library.MergeRoamingPageAsync(accountId.Value, creator.Uid,
                                sync.Seeded ? result.Items : result.Items.Take(30), finished, page + 1, started, revision, ct);
                            if (finished) break;
                        }
                        if (!finished) throw new ContentAccessException(ContentFailureKind.InvalidResponse, "Discovery page limit reached.");
                    }
                    catch (ContentAccessException ex)
                    {
                        failed++;
                        var message = FailureMessage(ex.Kind);
                        await _library.SetRoamingRefreshResultAsync(accountId.Value, creator.Uid, message,
                            expectedRevision: revision, token: ct);
                        if (ex.Kind == ContentFailureKind.AuthenticationRequired)
                        { _authenticationRequired = true; break; }
                    }
                }
                if (generation != Interlocked.Read(ref _generation)) return;
                await _library.SetRoamingRefreshResultAsync(accountId.Value, 0, null, failed == 0, revision, ct);
                Status = _authenticationRequired ? "需要重新登录；订阅与进度已保留" : failed > 0
                    ? $"部分来源更新失败（{failed} 位），已有内容仍可播放" : "订阅更新完成";
                _retryRefreshAt = _time.GetUtcNow().AddMinutes(5);
                if (_authenticationRequired)
                {
                    await _library.CancelPendingRoamingAsync(ct);
                    if (_library.Snapshot.Source == PlaybackSource.Roaming) await _library.PauseAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                if (generation == Interlocked.Read(ref _generation))
                { Status = "网络暂不可用，已保留本地订阅内容"; _retryRefreshAt = _time.GetUtcNow().AddMinutes(5); }
            }
            finally
            {
                if (generation == Interlocked.Read(ref _generation))
                { IsRefreshing = false; Changed?.Invoke(this, EventArgs.Empty); }
            }
        }
    }
    private static string FailureMessage(ContentFailureKind kind) => kind switch
    {
        ContentFailureKind.AuthenticationRequired => "需要重新登录",
        ContentFailureKind.RateLimited => "请求受到风控或限流，请稍后刷新",
        ContentFailureKind.Network => "网络暂不可用",
        _ => "来源暂不可用或返回数据异常"
    };

    public async Task StartAsync(CancellationToken token = default)
    {
        var intent = _library.RegisterRoamingIntent();
        var generation = Interlocked.Read(ref _generation);
        await RefreshAsync(token);
        if (generation == Interlocked.Read(ref _generation) && !_authenticationRequired)
            await _library.StartRoamingAsync(intent, token: token);
    }
    public async Task SetScheduleAsync(bool enabled, TimeSpan time, CancellationToken token = default)
    {
        await _library.SetRoamingScheduleAsync(enabled, time, token);
        _lastTick = _time.GetLocalNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void ResetClock()
    { _lastTick = _time.GetLocalNow(); _initialCheck = true; _library.RegisterRoamingIntent(); }

    public async Task TickAsync(CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            var now = _time.GetLocalNow(); var previous = _lastTick; _lastTick = now;
            var scheduleRevision = _library.RoamingScheduleRevision;
            var r = _library.Snapshot.Roaming;
            if (_accountId is null || r?.AccountId != _accountId || r.Subscriptions.Count == 0) return;
            var date = DateOnly.FromDateTime(now.DateTime);
            var due = new DateTimeOffset(now.Date + r.ScheduleTime, now.Offset);
            if (r.ScheduleEnabled && r.LastScheduleDate != date && previous < due && due <= now)
            {
                var request = await _library.TryBeginScheduledRoamingAsync(r.AccountId!.Value, date, r.ScheduleTime, scheduleRevision, token);
                await RefreshAsync(token);
                if (request is { } intent && !_authenticationRequired)
                    await _library.CompleteScheduledRoamingAsync(intent, token);
            }
            else if ((_initialCheck || now.TimeOfDay >= TimeSpan.FromHours(9)) && r.LastRefreshDate != date &&
                _time.GetUtcNow() >= _retryRefreshAt)
                await RefreshAsync(token);
            _initialCheck = false;
        }
        finally { Volatile.Write(ref _ticking, 0); }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); CancelRefresh();
        Task[] tasks; lock (_sync) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
