using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class MediaSelectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MediaSession SessionFor(ContentKey key, MediaSelectionPreferences selection) =>
        new(key, new(new Uri($"https://example.invalid/{selection.VideoQualityId ?? "auto"}-{selection.AudioQualityId ?? "auto"}"),
            "avc1.640028", 80), null, new Dictionary<string, string>(), DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task SwitchWhilePlayingKeepsPositionPauseStateVolumeItemAndCounts()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(17), Token);
        await f.Session.SetVolumeAsync(42, Token);
        var currentId = f.Session.Snapshot.Playback.CurrentId;
        var queue = f.Session.Snapshot.Queue.ToArray();

        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "120"), Token);

        Assert.Equal(new MediaSelectionPreferences(VideoQualityId: "120"), f.Content.Selections[^1]);
        Assert.Equal(2, f.Player.Loads.Count);
        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.Equal(queue, f.Session.Snapshot.Queue);
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        Assert.Equal(TimeSpan.FromSeconds(17), f.Player.Loads[^1].Position);
        Assert.False(f.Player.Loads[^1].Paused);
        Assert.Equal(42, f.Player.Loads[^1].Volume);
        Assert.Equal("https://example.invalid/120-auto", f.Player.Loads[^1].Session.Video.Url.AbsoluteUri);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        Assert.Contains("已切换", f.Session.Snapshot.Notice);
    }

    [Fact]
    public async Task SwitchWhilePausedStaysPausedAtTheSamePosition()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(9), Token);
        await f.Session.PauseAsync(Token);

        await f.Session.SetMediaSelectionAsync(new(AudioQualityId: "30280"), Token);

        Assert.True(f.Player.Loads[^1].Paused);
        Assert.True(f.Player.Paused);
        Assert.Equal(TimeSpan.FromSeconds(9), f.Player.Loads[^1].Position);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        Assert.Contains("保持暂停", f.Session.Snapshot.Notice);
    }

    [Fact]
    public async Task SelectionWhileStoppedOnlyAppliesWhenPlaybackStarts()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "64"), Token);

        Assert.Empty(f.Player.Loads); // Nothing starts playing by itself.

        await f.Session.PlayAsync(token: Token);
        Assert.Equal(new MediaSelectionPreferences(VideoQualityId: "64"), f.Content.Selections[^1]);
        Assert.Equal("https://example.invalid/64-auto", f.Player.Loads[^1].Session.Video.Url.AbsoluteUri);
    }

    [Fact]
    public async Task FailedResolutionDuringSwitchKeepsTheOldPlayback()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = (key, selection) => selection.VideoQualityId == "120"
            ? throw new ContentAccessException(ContentFailureKind.Removed, "Test missing tier")
            : SessionFor(key, selection);
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(5), Token);

        await Assert.ThrowsAsync<ContentAccessException>(() =>
            f.Session.SetMediaSelectionAsync(new(VideoQualityId: "120"), Token));

        Assert.Single(f.Player.Loads); // The old stream was never replaced.
        Assert.Equal(TimeSpan.FromSeconds(5), f.Player.Position); // Old playback keeps its position.
        Assert.Equal(5, f.Session.Snapshot.Playback.PositionSeconds);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(0, f.Session.Snapshot.Queue.Sum(q => q.CompletedCount));

        // A subsequent valid switch still applies normally.
        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "64"), Token);
        Assert.Equal(2, f.Player.Loads.Count);
    }

    [Fact]
    public async Task FailedLoadDuringSwitchRestoresThePreviousSelectionOnce()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(13), Token);
        f.Player.FailLoadsRemaining = 1; // The new stream's load fails; the restore load succeeds.

        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "120"), Token);

        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(TimeSpan.FromSeconds(13), f.Player.Loads[^1].Position);
        Assert.Equal("https://example.invalid/auto-auto", f.Player.Loads[^1].Session.Video.Url.AbsoluteUri);
        Assert.Equal(new MediaSelectionPreferences(VideoQualityId: "120"), f.Content.Selections[1]); // Attempted preference…
        Assert.Equal(MediaSelectionPreferences.Automatic, f.Content.Selections[2]); // …restored once for playback only.
        Assert.Contains("恢复原画质", f.Session.Snapshot.Notice);
    }

    [Fact]
    public async Task UnrecoverableLoadFailurePausesInPlaceRetainingThePosition()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(21), Token);
        var currentId = f.Session.Snapshot.Playback.CurrentId;
        f.Player.FailLoadsRemaining = 2; // The new stream and the restore both fail.

        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "120"), Token);

        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(21, f.Session.Snapshot.Playback.PositionSeconds);
        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.Single(f.Player.Loads); // Only the initial load remains.
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        Assert.Contains("已暂停", f.Session.Snapshot.Notice);
    }

    [Fact]
    public async Task StaleEofFromTheOldStreamCannotAdvanceTheQueueAfterSwitch()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        var stale = f.Player.Generation;

        await f.Session.SetMediaSelectionAsync(new(VideoQualityId: "80"), Token);
        f.Player.End("eof", generation: stale); // Late event of the replaced stream.
        await f.Session.CheckpointAsync(Token);

        Assert.Equal(2, f.Player.Loads.Count); // No advancement replaced the switched stream.
        Assert.Equal(0, f.Session.Snapshot.Queue.Sum(q => q.CompletedCount));
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task AuthenticationRequiredPausesWithoutMarkingTheEntryOrSkippingTheQueue()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(11), Token);
        var currentId = f.Session.Snapshot.Playback.CurrentId;
        f.Content.ResolveFailures[f.Session.Snapshot.Current!.Key] = ContentFailureKind.AuthenticationRequired;

        await f.Session.ReloadAsync(Token); // The account changed mid-playback and the content is gated.

        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(11, f.Session.Snapshot.Playback.PositionSeconds);
        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.NotEqual(EntryAvailability.Failed, f.Session.Snapshot.Current!.Availability); // Not marked invalid.
        Assert.Equal(0, f.Session.Snapshot.Queue.Sum(q => q.CompletedCount));
        Assert.Contains("需要登录", f.Session.Snapshot.Notice);

        f.Content.ResolveFailures.Clear();
        await f.Session.PlayAsync(token: Token); // The same item resumes at its position.
        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task AccountChangeReloadsTheCurrentItemWithItsPositionAndIntent()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(13), Token);
        var currentId = f.Session.Snapshot.Playback.CurrentId;

        await f.Session.ReloadAsync(Token); // What the window does after login or logout.

        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.Equal(2, f.Player.Loads.Count);
        Assert.Equal(TimeSpan.FromSeconds(13), f.Player.Loads[^1].Position);
        Assert.False(f.Player.Loads[^1].Paused);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task PausedAccountChangeReloadsStayPausedWithoutAdvancing()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(7), Token);
        await f.Session.PauseAsync(Token);
        var currentId = f.Session.Snapshot.Playback.CurrentId;

        await f.Session.ReloadAsync(Token);

        Assert.Equal(currentId, f.Session.Snapshot.Playback.CurrentId);
        Assert.True(f.Player.Loads[^1].Paused);
        Assert.Equal(TimeSpan.FromSeconds(7), f.Player.Loads[^1].Position);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task ConstructorSelectionDrivesInitialPlayback()
    {
        await using var f = new LibraryFixture(selection: new(VideoQualityId: "64"));
        await f.SeedAsync();
        f.Content.CustomResolve = SessionFor;

        await f.Session.PlayAsync(token: Token);

        Assert.Equal(new MediaSelectionPreferences(VideoQualityId: "64"), f.Content.Selections[^1]);
        Assert.Equal("https://example.invalid/64-auto", f.Player.Loads[^1].Session.Video.Url.AbsoluteUri);
    }
}
