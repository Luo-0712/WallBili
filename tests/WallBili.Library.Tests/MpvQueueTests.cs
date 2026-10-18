using System.Diagnostics;
using WallBili.Core;
using WallBili.Player.Mpv;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class MpvQueueTests
{
    [Fact]
    public async Task RealMpvScheduledRoamingCompletesPartsRotatesAndKeepsOneProcess()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        var clock = new RoamingClock();
        await using var f = new LibraryFixture(clock);
        Directory.CreateDirectory(f.DirectoryPath);
        var media = Path.Combine(f.DirectoryPath, "roaming.mp4");
        await GenerateAsync(media, "1.2", ct);
        f.Content.MediaUri = new Uri(media);
        var player = new MpvQueuePlayer(() => new() { Headless = true });
        await using var session = new LibrarySession(f.Content, f.Store, player, clock);
        await using var service = new RoamingService(session, new DiscoveryStub(), clock);
        await session.InitializeAsync(ct);
        await session.ImportCollectionAsync(new(1, 10), ct);
        await session.EnqueueAsync(session.Snapshot.Entries.Select(e => e.Key), ct);
        await service.SetAccountAsync(99, ct);
        await service.SaveSubscriptionsAsync([new(1, "UP")], ct);
        await service.SetScheduleAsync(true, TimeSpan.FromHours(9), ct);
        await session.PlayAsync(token: ct);
        var pid = await player.GetProcessIdAsync(ct);
        clock.Now = clock.Now.AddMinutes(2);
        await service.TickAsync(ct);
        Assert.True(session.Snapshot.Roaming!.PendingStart);
        while (session.Snapshot.Source != PlaybackSource.Roaming) await Task.Delay(20, ct);
        Assert.Equal(pid, await player.GetProcessIdAsync(ct));
        while (session.Snapshot.Current?.Key.Bvid != TestContent.B) await Task.Delay(20, ct);
        Assert.NotNull(session.Snapshot.Roaming!.Videos.Single(v => v.Video.Bvid == TestContent.A).CompletedAt);
        await session.PauseAsync(ct);
        await session.PlayManualAsync(token: ct);
        Assert.Equal(TestContent.B, session.Snapshot.Current!.Key.Bvid);
        Assert.Equal(pid, await player.GetProcessIdAsync(ct));
        await service.StartAsync(ct);
        Assert.Equal(PlaybackSource.Roaming, session.Snapshot.Source);
        Assert.Equal(TestContent.B, session.Snapshot.Current!.Key.Bvid);
        Assert.Equal(pid, await player.GetProcessIdAsync(ct));
        await session.StopAsync(ct);
        await service.TickAsync(ct);
        Assert.Equal(PlaybackIntent.Stopped, session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task RealProcessDeathAndOutputReplacementRestorePositionWithoutParallelAudioProcesses()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        await using var f = new LibraryFixture();
        Directory.CreateDirectory(f.DirectoryPath);
        var media = Path.Combine(f.DirectoryPath, "recovery.mp4");
        await GenerateAsync(media, "8", ct);
        f.Content.MediaUri = new Uri(media);
        var player = new MpvQueuePlayer(() => new() { Headless = true });
        await using (var session = new LibrarySession(f.Content, f.Store, player))
        {
            await session.InitializeAsync(ct);
            await session.ImportCollectionAsync(new(1, 10), ct);
            await session.EnqueueAsync(session.Snapshot.Entries.Select(e => e.Key), ct);
            await session.PlayAsync(token: ct);
            var firstPid = (await player.GetProcessIdAsync(ct))!.Value;
            for (var i = 0; i < 20; i++)
            {
                var target = session.Snapshot.Queue[i % session.Snapshot.Queue.Count].Id;
                await session.PlayAsync(target, ct);
                Assert.Equal(target, session.Snapshot.Playback.CurrentId);
                Assert.Equal(firstPid, await player.GetProcessIdAsync(ct));
            }
            await session.SeekAsync(TimeSpan.FromSeconds(2), ct);
            await session.PauseAsync(ct);
            await session.CheckpointAsync(ct);
            var current = session.Snapshot.Playback.CurrentId;
            using (var process = Process.GetProcessById(firstPid)) { process.Kill(); await process.WaitForExitAsync(ct); }
            while (!session.Snapshot.RecoveryPending) await Task.Delay(20, ct);
            await session.RetryRecoveryAsync(immediate: true, token: ct);
            var secondPid = (await player.GetProcessIdAsync(ct))!.Value;
            Assert.NotEqual(firstPid, secondPid);
            Assert.Equal(current, session.Snapshot.Playback.CurrentId);
            Assert.Equal(PlaybackIntent.Paused, session.Snapshot.Playback.Intent);
            await session.CheckpointAsync(ct);
            Assert.InRange(session.Snapshot.Playback.PositionSeconds, 1.9, 2.1);
            await session.RebindOutputAsync(ct);
            var thirdPid = (await player.GetProcessIdAsync(ct))!.Value;
            Assert.NotEqual(secondPid, thirdPid);
            Assert.False(ProcessExists(secondPid));
            await session.SuspendAsync(ct);
            await session.ResumeAsync(ct);
            var resumedPid = (await player.GetProcessIdAsync(ct))!.Value;
            await session.CheckpointAsync(ct);
            Assert.InRange(session.Snapshot.Playback.PositionSeconds, 1.9, 2.1);
            Assert.All(session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
            await session.DisposeAsync();
            Assert.False(ProcessExists(thirdPid));
            Assert.False(ProcessExists(resumedPid));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDecoderAdvancesAcrossTwoSourcesAndSkipsFailedMediaWithoutFalseCompletion(bool missingFirst)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        await using var f = new LibraryFixture();
        Directory.CreateDirectory(f.DirectoryPath);
        var media = Path.Combine(f.DirectoryPath, "fixture.mp4");
        await GenerateAsync(media, "0.7", ct);
        f.Content.MediaUri = new Uri(media);
        if (missingFirst) f.Content.MediaUris[new(TestContent.A, 11)] = new Uri(Path.Combine(f.DirectoryPath, "missing.mp4"));
        var player = new MpvQueuePlayer(() => new() { Headless = true });
        await using (var session = new LibrarySession(f.Content, f.Store, player))
        {
            await session.InitializeAsync(ct);
            await session.ImportCollectionAsync(new(1, 10), ct);
            await session.ImportCollectionAsync(new(2, 20), ct);
            await session.EnqueueAsync(session.Snapshot.Entries.Select(e => e.Key), ct);
            if (!missingFirst)
            {
                // Several replacements generate native stop events. Those events must not advance the new queue item.
                await session.PlayAsync(session.Snapshot.Queue[1].Id, ct);
                await session.PlayAsync(session.Snapshot.Queue[2].Id, ct);
            }
            await session.PlayAsync(session.Snapshot.Queue[0].Id, ct);
            while (session.Snapshot.Playback.Intent != PlaybackIntent.Stopped) await Task.Delay(25, ct);
            Assert.Equal(missingFirst ? 3 : 4, session.Snapshot.Queue.Sum(q => q.CompletedCount));
            Assert.Equal(missingFirst ? EntryAvailability.Failed : EntryAvailability.Available, session.Snapshot.Entries[0].Availability);
            Assert.Equal(missingFirst ? 0 : 1, session.Snapshot.Queue[0].CompletedCount);
            Assert.All(session.Snapshot.Queue.Skip(1), q => Assert.Equal(1, q.CompletedCount));
        }
        var saved = await f.Store.LoadAsync(ct);
        Assert.Equal(4, saved.Queue.Count);
        Assert.Equal(missingFirst ? 3 : 4, saved.Queue.Sum(q => q.CompletedCount));
    }

    private static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task GenerateAsync(string media, string seconds, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", seconds, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", media })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(process.ExitCode == 0, "Fixture generation failed: " + await error);
    }
}
