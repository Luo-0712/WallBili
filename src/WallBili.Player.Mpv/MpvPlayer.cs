using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WallBili.Core;

namespace WallBili.Player.Mpv;

/// <summary>
/// Owns one mpv process and one playback clock, including separate DASH video/audio tracks.
/// IPC uses a Windows named pipe or a Unix-domain socket. No media URLs are placed in argv.
/// </summary>
public sealed class MpvPlayer : IAsyncDisposable
{
    private readonly MpvPlayerOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _loads = new(1, 1);
    private readonly SemaphoreSlim _seeks = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _loadSync = new();
    private Process? _process;
    private SafeFileHandle? _processJob;
    private Stream? _connection;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private TaskCompletionSource<bool>? _loadCompletion;
    private TaskCompletionSource<bool>? _seekCompletion;
    private bool _seekingStarted;
    private bool _loadingStarted;
    private string? _endpoint;
    private string? _socketDirectory;
    private long _nextRequest;
    private bool _disposed;

    public MpvPlayer(MpvPlayerOptions? options = null)
    {
        _options = options ?? new();
        if (string.IsNullOrWhiteSpace(_options.ExecutablePath) ||
            _options.WindowId is <= 0 || (_options.WindowId is not null && !OperatingSystem.IsWindows()) ||
            _options.StartupTimeout <= TimeSpan.Zero || _options.CommandTimeout <= TimeSpan.Zero ||
            _options.LoadTimeout <= TimeSpan.Zero)
            throw new ArgumentException("A player executable and positive timeouts are required.", nameof(options));
    }

    /// <remarks>Raised on the IPC reader. Handlers should dispatch UI work and must not block.</remarks>
    public event EventHandler<MpvEventArgs>? EventReceived;
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    public event EventHandler? ConnectionClosed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false } && _writer is not null)
                return;
            await CleanupAsync().ConfigureAwait(false);
            var id = "wallbili-" + Guid.NewGuid().ToString("N");
            // Isolate optional user scripts as well as config, including mpv builds without Lua.
            _socketDirectory = Path.Combine(Path.GetTempPath(), id);
            Directory.CreateDirectory(_socketDirectory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsWindows())
                _endpoint = @"\\.\pipe\" + id;
            else
            {
                // A short, private directory keeps Unix socket paths below their platform limit.
                _endpoint = Path.Combine(_socketDirectory, "ipc");
            }

            var startInfo = new ProcessStartInfo(_options.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "--no-config", "--config-dir=" + _socketDirectory, "--idle=yes", "--keep-open=no",
                "--input-terminal=no", "--terminal=no", "--msg-level=all=no",
                "--input-ipc-server=" + _endpoint,
                "--pause=" + (_options.StartPaused ? "yes" : "no")
            })
                startInfo.ArgumentList.Add(argument);
            // Full Windows mpv builds expose the built-in ytdl hook. Resources are already resolved
            // by our native adapter. The cloud validation build has no Lua or ytdl hook at all.
            if (OperatingSystem.IsWindows()) startInfo.ArgumentList.Add("--ytdl=no");
            if (_options.WindowId is { } windowId)
            {
                startInfo.ArgumentList.Add("--wid=" + windowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--input-default-bindings=no");
                startInfo.ArgumentList.Add("--input-cursor=no");
                startInfo.ArgumentList.Add("--input-vo-keyboard=no");
                startInfo.ArgumentList.Add("--osc=no");
            }
            startInfo.ArgumentList.Add("--panscan=" + (_options.FillWindow ? "1" : "0"));
            if (_options.Headless)
            {
                startInfo.ArgumentList.Add("--vo=null");
                startInfo.ArgumentList.Add("--ao=null");
                startInfo.ArgumentList.Add("--hwdec=no");
            }
            else
                startInfo.ArgumentList.Add("--force-window=yes");

            try
            {
                _process = Process.Start(startInfo) ?? throw new MpvException(MpvErrorCode.StartupFailed, "Could not start mpv.");
                if (OperatingSystem.IsWindows()) _processJob = WindowsProcessJob.Attach(_process);
                // Discard diagnostics: mpv may include temporary signed CDN URLs in them.
                _process.OutputDataReceived += static (_, _) => { };
                _process.ErrorDataReceived += static (_, _) => { };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new MpvException(MpvErrorCode.ExecutableNotFound, "mpv could not be launched. Check the configured executable path.");
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(_options.StartupTimeout);
            try
            {
                _connection = await ConnectAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MpvException(MpvErrorCode.Timeout, "Timed out connecting to mpv IPC.");
            }
            _writer = new StreamWriter(_connection, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReadMessagesAsync(_connection, _readerCancellation.Token);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task LoadAsync(MediaSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _loads.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Native arrays preserve commas/colons in URLs and headers without mpv list escaping.
            await CommandAsync(["set_property", "http-header-fields", session.Headers.Select(pair => pair.Key + ": " + pair.Value).ToArray()], cancellationToken).ConfigureAwait(false);
            await CommandAsync(["set_property", "audio-files", session.Audio is { } audio ? new[] { audio.Url.AbsoluteUri } : Array.Empty<string>()], cancellationToken).ConfigureAwait(false);
            lock (_loadSync)
            {
                _loadingStarted = false;
                _loadCompletion = completion;
            }
            await CommandAsync(["loadfile", session.Video.Url.AbsoluteUri, "replace"], cancellationToken).ConfigureAwait(false);
            try
            {
                await completion.Task.WaitAsync(_options.LoadTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new MpvException(MpvErrorCode.Timeout, "Timed out loading media in mpv.");
            }
        }
        finally
        {
            lock (_loadSync)
                if (ReferenceEquals(_loadCompletion, completion))
                    _loadCompletion = null;
            _loads.Release();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) => SetPauseAsync(true, cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => SetPauseAsync(false, cancellationToken);

    public async Task SetVideoFitAsync(bool fill, CancellationToken cancellationToken = default) =>
        await CommandAsync(["set_property", "panscan", fill ? 1 : 0], cancellationToken).ConfigureAwait(false);

    public async Task StopPlaybackAsync(CancellationToken cancellationToken = default) =>
        await CommandAsync(["stop"], cancellationToken).ConfigureAwait(false);

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
            throw new MpvException(MpvErrorCode.InvalidArgument, "Volume must be between 0 and 100.");
        await CommandAsync(["set_property", "volume", volume], cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero || !double.IsFinite(position.TotalSeconds))
            throw new MpvException(MpvErrorCode.InvalidArgument, "Seek position must be a non-negative time.");
        await _seeks.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_loadSync) { _seekCompletion = completion; _seekingStarted = false; }
            await CommandAsync(["seek", position.TotalSeconds, "absolute+exact"], cancellationToken).ConfigureAwait(false);
            // A command ACK means the seek was accepted, not that time-pos has moved. Saving immediately
            // after the ACK can overwrite a correct resume point with zero during a paused reload.
            try { await completion.Task.WaitAsync(_options.CommandTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new MpvException(MpvErrorCode.Timeout, "Timed out completing the seek."); }
        }
        finally
        {
            lock (_loadSync) if (ReferenceEquals(_seekCompletion, completion)) _seekCompletion = null;
            _seeks.Release();
        }
    }

    public async Task<T?> GetPropertyAsync<T>(string property, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(property))
            throw new MpvException(MpvErrorCode.InvalidArgument, "A property name is required.");
        var data = await CommandAsync(["get_property", property], cancellationToken).ConfigureAwait(false);
        try { return data.Deserialize<T>(); }
        catch (JsonException) { throw new MpvException(MpvErrorCode.CommandFailed, "The mpv property has an unexpected type."); }
    }

    /// <summary>Stops playback and closes this player's process. StartAsync can start a new one.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is not null && _process is { HasExited: false })
            {
                try
                {
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stop.CancelAfter(TimeSpan.FromSeconds(2));
                    await CommandAsync(["quit"], stop.Token).ConfigureAwait(false);
                    await _process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is MpvException or OperationCanceledException) { }
            }
            await CleanupAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CleanupAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task SetPauseAsync(bool paused, CancellationToken cancellationToken) =>
        await CommandAsync(["set_property", "pause", paused], cancellationToken).ConfigureAwait(false);

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new MpvException(MpvErrorCode.StartupFailed, "mpv exited before its IPC connection was ready.");
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", _endpoint![@"\\.\pipe\".Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch (TimeoutException) { pipe.Dispose(); }
                catch (IOException) { pipe.Dispose(); }
            }
            else
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint!), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> CommandAsync(object[] command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var writer = _writer ?? throw new MpvException(MpvErrorCode.Disconnected, "The mpv IPC connection is not running.");
        var requestId = Interlocked.Increment(ref _nextRequest);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CommandTimeout);
            try
            {
                await _writes.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    var payload = JsonSerializer.Serialize(new { command, request_id = requestId });
                    await writer.WriteLineAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
                }
                finally { _writes.Release(); }
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MpvException(MpvErrorCode.Timeout, "Timed out waiting for an mpv IPC command.");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                throw new MpvException(MpvErrorCode.Disconnected, "The mpv IPC connection was closed.");
            }
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task ReadMessagesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("request_id", out var id) && id.TryGetInt64(out var requestId) && _pending.TryRemove(requestId, out var completion))
                {
                    if (root.TryGetProperty("error", out var error) && error.GetString() != "success")
                        completion.TrySetException(new MpvException(MpvErrorCode.CommandFailed, "mpv rejected the IPC command or the property is unavailable."));
                    else
                        completion.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : JsonSerializer.SerializeToElement<object?>(null));
                }
                if (root.TryGetProperty("event", out var eventProperty) && eventProperty.GetString() is { } eventName)
                    DispatchEvent(eventName, root.Clone());
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            var disconnected = new MpvException(MpvErrorCode.Disconnected, "The mpv IPC connection was closed.");
            foreach (var request in _pending.Values)
                request.TrySetException(disconnected);
            lock (_loadSync)
            {
                _loadCompletion?.TrySetException(disconnected);
                _seekCompletion?.TrySetException(disconnected);
            }
            try { ConnectionClosed?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    private void DispatchEvent(string eventName, JsonElement data)
    {
        lock (_loadSync)
        {
            if (eventName == "start-file") _loadingStarted = true;
            if (eventName == "file-loaded") _loadCompletion?.TrySetResult(true);
            if (eventName == "seek") _seekingStarted = true;
            if (eventName == "playback-restart" && _seekingStarted) _seekCompletion?.TrySetResult(true);
            if (eventName == "end-file" && data.TryGetProperty("reason", out var seekEnd))
            {
                if (seekEnd.GetString() == "eof") _seekCompletion?.TrySetResult(true);
                else _seekCompletion?.TrySetException(new MpvException(MpvErrorCode.MediaFailed, "Playback ended during seek."));
            }
            if (eventName == "end-file" && _loadingStarted && data.TryGetProperty("reason", out var loadReason) && loadReason.GetString() == "error")
                _loadCompletion?.TrySetException(new MpvException(MpvErrorCode.MediaFailed, "mpv could not load the requested media."));
        }
        if (eventName == "end-file")
        {
            var reason = data.TryGetProperty("reason", out var value) ? value.GetString() ?? "unknown" : "unknown";
            var entryId = data.TryGetProperty("playlist_entry_id", out var entry) && entry.TryGetInt64(out var id) ? (long?)id : null;
            RaiseSafely(PlaybackEnded, new PlaybackEndedEventArgs(reason, reason == "error", entryId));
        }
        RaiseSafely(EventReceived, new MpvEventArgs(eventName, data));
    }

    private void RaiseSafely<T>(EventHandler<T>? handlers, T args) where T : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, args); }
            catch { /* A consumer must not terminate IPC correlation or process ownership. */ }
    }

    private static void ValidateSession(MediaSession session)
    {
        if (!ValidMediaUri(session.Video.Url) || (session.Audio is { } audio && !ValidMediaUri(audio.Url)))
            throw new MpvException(MpvErrorCode.InvalidSession, "Media must use an absolute HTTP, HTTPS or local file URI.");
        foreach (var pair in session.Headers)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                pair.Value.Contains('\r') || pair.Value.Contains('\n') || pair.Value.Contains('\0'))
                throw new MpvException(MpvErrorCode.InvalidSession, "The media session contains an invalid HTTP header.");
    }

    private static bool ValidMediaUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "https" or "http" or "file";

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new MpvException(MpvErrorCode.Disposed, "This mpv player has been disposed.");
    }

    private async Task CleanupAsync()
    {
        _readerCancellation?.Cancel();
        try { _writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        _writer = null;
        if (_connection is not null)
            try { await _connection.DisposeAsync().ConfigureAwait(false); } catch (IOException) { } catch (ObjectDisposedException) { }
        _connection = null;
        if (_readerTask is not null)
            try { await _readerTask.ConfigureAwait(false); } catch { }
        _readerTask = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); _process = null; }
        }
        if (_socketDirectory is { } directory)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            _socketDirectory = null;
        }
        _endpoint = null;
        _processJob?.Dispose();
        _processJob = null;
    }
}
