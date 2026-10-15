using System.Diagnostics;
using System.Text.Json;
using WallBili.Core;
using Xunit;

namespace WallBili.Player.Mpv.Tests;

public sealed class MpvPlayerTests
{
    [Fact]
    public async Task MissingExecutableIsClassifiedWithoutLeakingRawError()
    {
        await using var player = new MpvPlayer(new() { ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-secret-token") });
        var exception = await Assert.ThrowsAsync<MpvException>(() => player.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MpvErrorCode.ExecutableNotFound, exception.Code);
        Assert.DoesNotContain("secret-token", exception.ToString());
    }

    [Fact]
    public async Task InvalidHeadersAreRejectedBeforeLaunchingPlayer()
    {
        await using var player = new MpvPlayer(new() { ExecutablePath = "not-needed" });
        var session = Session(new Uri("https://example.invalid/video?secret-token"), null,
            new Dictionary<string, string> { ["Referer"] = "safe\r\nInjected: yes" });
        var exception = await Assert.ThrowsAsync<MpvException>(() => player.LoadAsync(session, TestContext.Current.CancellationToken));
        Assert.Equal(MpvErrorCode.InvalidSession, exception.Code);
        Assert.DoesNotContain("secret-token", exception.ToString());
        Assert.DoesNotContain("Injected", exception.ToString());
    }

    [Fact]
    public async Task RealSplitH264AacPlaybackSupportsPauseSeekEventsAndCleanRestart()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var cancellationToken = timeout.Token;
        var executable = await RequireExecutableAsync(Environment.GetEnvironmentVariable("MPV_PATH") ?? "mpv");
        var ffmpeg = await RequireExecutableAsync(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg");
        var directory = Path.Combine(Path.GetTempPath(), "wallbili-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "video.mp4");
            var audio = Path.Combine(directory, "audio.m4a");
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12", "-t", "6", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", video], cancellationToken);
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6", "-vn", "-c:a", "aac", audio], cancellationToken);

            await using var player = new MpvPlayer(new() { ExecutablePath = executable, Headless = true, StartPaused = true });
            var ended = new TaskCompletionSource<PlaybackEndedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackEnded += (_, args) => { if (args.Reason == "eof") ended.TrySetResult(args); };
            await player.LoadAsync(Session(new Uri(video), new Uri(audio)), cancellationToken);

            Assert.True(await player.GetPropertyAsync<bool>("pause", cancellationToken));
            var tracks = await player.GetPropertyAsync<JsonElement>("track-list", cancellationToken);
            Assert.Contains(tracks.EnumerateArray(), track => track.GetProperty("type").GetString() == "video" && track.GetProperty("selected").GetBoolean());
            Assert.Contains(tracks.EnumerateArray(), track => track.GetProperty("type").GetString() == "audio" && track.GetProperty("selected").GetBoolean() && track.GetProperty("external").GetBoolean());
            var videoParams = await player.GetPropertyAsync<JsonElement>("video-params", cancellationToken);
            Assert.Equal(160, videoParams.GetProperty("w").GetInt32());
            var audioParams = await player.GetPropertyAsync<JsonElement>("audio-params", cancellationToken);
            Assert.Equal(48000, audioParams.GetProperty("samplerate").GetInt32());

            // Parallel requests exercise request_id matching while unsolicited events arrive.
            var durations = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => player.GetPropertyAsync<double>("duration", cancellationToken)));
            Assert.All(durations, value => Assert.InRange(value, 5.9, 6.1));
            var invalid = await Assert.ThrowsAsync<MpvException>(() => player.GetPropertyAsync<JsonElement>("nonexistent-secret-token", cancellationToken));
            Assert.Equal(MpvErrorCode.CommandFailed, invalid.Code);
            Assert.DoesNotContain("secret-token", invalid.ToString());

            await player.ResumeAsync(cancellationToken);
            await WaitUntilAsync(async () => await player.GetPropertyAsync<double>("time-pos", cancellationToken) > .3, cancellationToken);
            await player.PauseAsync(cancellationToken);
            var position = await player.GetPropertyAsync<double>("time-pos", cancellationToken);
            await Task.Delay(180, cancellationToken);
            Assert.InRange(Math.Abs(await player.GetPropertyAsync<double>("time-pos", cancellationToken) - position), 0, .1);
            await player.SeekAsync(TimeSpan.FromSeconds(4), cancellationToken);
            await WaitUntilAsync(async () => await player.GetPropertyAsync<double>("time-pos", cancellationToken) >= 3.9, cancellationToken);
            await player.ResumeAsync(cancellationToken);
            Assert.False((await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)).IsError);

            var pid = await player.GetPropertyAsync<int>("pid", cancellationToken);
            await player.StopAsync(cancellationToken);
            Assert.False(ProcessExists(pid));
            await player.StartAsync(cancellationToken);
            Assert.NotEqual(pid, await player.GetPropertyAsync<int>("pid", cancellationToken));

            var missing = await Assert.ThrowsAsync<MpvException>(() => player.LoadAsync(Session(new Uri(Path.Combine(directory, "missing-secret-token.mp4")), null), cancellationToken));
            Assert.Equal(MpvErrorCode.MediaFailed, missing.Code);
            Assert.DoesNotContain("secret-token", missing.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static MediaSession Session(Uri video, Uri? audio, IReadOnlyDictionary<string, string>? headers = null) =>
        new(new ContentKey("BV1test000000", 1), new MediaTrack(video, "avc1", 16),
            audio is null ? null : new MediaTrack(audio, "mp4a", 0),
            headers ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow, null);

    private static async Task<string> RequireExecutableAsync(string executable)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(executable.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ? "-version" : "--version");
            using var process = Process.Start(info)!;
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return executable;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Real playback test requires mpv and FFmpeg on PATH, or MPV_PATH / FFMPEG_PATH.");
            return executable;
        }
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, "Fixture generation failed: " + await error);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition()) await Task.Delay(50, cancellationToken);
    }

    private static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
