using WallBili.Core;

namespace WallBili.Player.Mpv;

/// <summary>Correlates native playlist entry IDs with queue generations; late stop/EOF events cannot skip a new item.</summary>
public sealed class MpvQueuePlayer(Func<MpvPlayerOptions>? options = null) : IQueuePlayer
{
    private readonly object _sync = new();
    private readonly Dictionary<long, Guid> _generations = [];
    private MpvPlayer? _player;
    private MpvPlayerOptions? _currentOptions;
    private Guid? _loading;
    private Guid? _active;
    public event EventHandler<QueuePlaybackEnded>? Ended;

    public async Task LoadAsync(MediaSession session, Guid playbackId, TimeSpan position, bool paused, double volume,
        CancellationToken cancellationToken = default)
    {
        var desired = (options?.Invoke() ?? new()) with { StartPaused = true };
        if (_player is not null && desired != _currentOptions) await DisposeAsync();
        if (_player is null)
        {
            _player = new(desired);
            _currentOptions = desired;
            _player.EventReceived += OnEvent;
            _player.PlaybackEnded += OnEnd;
            _player.ConnectionClosed += OnDisconnect;
        }
        await _player.StartAsync(cancellationToken);
        await _player.PauseAsync(cancellationToken);
        lock (_sync) { _loading = playbackId; _active = playbackId; }
        try
        {
            await _player.LoadAsync(session, cancellationToken);
            await _player.SetVolumeAsync(volume, cancellationToken);
            if (position > TimeSpan.Zero) await _player.SeekAsync(position, cancellationToken);
            if (!paused) await _player.ResumeAsync(cancellationToken);
        }
        catch (MpvException ex) when (ex.Code == MpvErrorCode.MediaFailed)
        {
            throw new ContentAccessException(ContentFailureKind.Unsupported, "The player could not decode this media.");
        }
        finally { lock (_sync) _loading = null; }
    }

    private void OnEvent(object? sender, MpvEventArgs args)
    {
        if (args.EventName != "start-file") return;
        lock (_sync)
            if (_loading is { } generation && args.Data.TryGetProperty("playlist_entry_id", out var id))
                _generations[id.GetInt64()] = generation;
    }

    private void OnEnd(object? sender, PlaybackEndedEventArgs args)
    {
        Guid generation;
        lock (_sync)
        {
            if (args.PlaylistEntryId is not { } id || !_generations.Remove(id, out generation)) return;
        }
        Ended?.Invoke(this, new(generation, args.Reason));
    }

    private void OnDisconnect(object? sender, EventArgs args)
    {
        Guid? active;
        lock (_sync) { active = _active; _active = null; _generations.Clear(); }
        if (active is { } id) Ended?.Invoke(this, new(id, "disconnect"));
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        paused ? _player!.PauseAsync(cancellationToken) : _player!.ResumeAsync(cancellationToken);
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => _player!.SeekAsync(position, cancellationToken);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => _player!.SetVolumeAsync(volume, cancellationToken);
    public async Task<TimeSpan?> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        if (_player is null) return null;
        try
        {
            var seconds = await _player.GetPropertyAsync<double?>("time-pos", cancellationToken);
            return seconds is { } value && double.IsFinite(value) && value >= 0 ? TimeSpan.FromSeconds(value) : null;
        }
        catch (MpvException) { return null; } // EOF/expired media may lose time-pos; retain the previous checkpoint.
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) { _active = null; _loading = null; _generations.Clear(); }
        if (_player is not null)
        {
            try { await _player.StopPlaybackAsync(cancellationToken); }
            catch (MpvException) { await DisposeAsync(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _active = null; _loading = null; _generations.Clear(); }
        if (_player is null) return;
        _player.EventReceived -= OnEvent;
        _player.PlaybackEnded -= OnEnd;
        _player.ConnectionClosed -= OnDisconnect;
        await _player.DisposeAsync();
        _player = null;
    }
}
