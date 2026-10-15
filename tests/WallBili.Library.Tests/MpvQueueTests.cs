using System.Diagnostics;
using WallBili.Core;
using WallBili.Player.Mpv;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class MpvQueueTests
{
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
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "0.7", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", media })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(process.ExitCode == 0, "Fixture generation failed: " + await error);
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
}
