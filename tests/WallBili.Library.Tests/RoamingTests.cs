using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class RoamingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task RoamingPreservesManualPositionAndPlaysPartsThenOldContent()
    {
        await using var f = new LibraryFixture();
        await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(17), Token);
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [
            new(TestContent.A, 1, "UP", "New", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(120)),
            new(TestContent.B, 1, "UP", "Old", DateTimeOffset.UtcNow.AddDays(-10), TimeSpan.FromSeconds(60))], true, token: Token);
        await f.Session.StartRoamingAsync(token: Token);
        Assert.Equal(PlaybackSource.Roaming, f.Session.Snapshot.Source);
        Assert.Equal(new ContentKey(TestContent.A, 11), f.Session.Snapshot.Current!.Key);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Current?.Key.Cid == 12);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Current?.Key.Bvid == TestContent.B);
        await f.Session.PlayManualAsync(token: Token);
        Assert.Equal(PlaybackSource.Manual, f.Session.Snapshot.Source);
        Assert.Equal(TimeSpan.FromSeconds(17), f.Player.Loads[^1].Position);
    }

    [Fact]
    public async Task SkipExcludesWholeVideoForThisRoundAndStopStartsNewRound()
    {
        await using var f = new LibraryFixture();
        await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.A, 1, "UP", "A", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(120))], true, token: Token);
        await f.Session.StartRoamingAsync(token: Token);
        await f.Session.NextAsync(Token);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
        await f.Session.StartRoamingAsync(token: Token);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task ConsumedOldVideosRotateWithoutStarvingThirdVideoAtSameClockTime()
    {
        var clock = new RoamingClock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [
            new(TestContent.B, 1, "UP", "B", clock.Now.AddDays(-20), TimeSpan.FromSeconds(60)),
            new(TestContent.C, 1, "UP", "C", clock.Now.AddDays(-21), TimeSpan.FromSeconds(60)),
            new(TestContent.D, 1, "UP", "D", clock.Now.AddDays(-22), TimeSpan.FromSeconds(60))], true, token: Token);
        await f.Session.StartRoamingAsync(token: Token);
        for (var i = 0; i < 8; i++)
        {
            var count = f.Player.Loads.Count; f.Player.End();
            await UntilAsync(() => f.Player.Loads.Count > count);
        }
        Assert.Equal(3, f.Player.Loads.Skip(3).Take(3).Select(l => l.Key.Bvid).Distinct().Count());
        Assert.Equal(3, f.Player.Loads.Skip(6).Take(3).Select(l => l.Key.Bvid).Distinct().Count());
    }

    [Fact]
    public async Task SwitchingAccountRejectsLatePagesAndKeepsIndependentSubscriptions()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        var revision = f.Session.RoamingRevision;
        await f.Session.SetRoamingScheduleAsync(true, TimeSpan.FromHours(10), Token);
        await f.Session.SetRoamingAccountAsync(100, Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.B, 1, "UP", "B", DateTimeOffset.UtcNow, TimeSpan.Zero)],
            true, expectedRevision: revision, token: Token);
        Assert.Empty(f.Session.Snapshot.Roaming!.Videos);
        Assert.Empty(f.Session.Snapshot.Roaming.Subscriptions);
        Assert.False(f.Session.Snapshot.Roaming.ScheduleEnabled);
        await f.Session.SetRoamingAccountAsync(99, Token);
        Assert.Single(f.Session.Snapshot.Roaming!.Subscriptions);
        Assert.True(f.Session.Snapshot.Roaming.ScheduleEnabled);
        Assert.Empty(f.Session.Snapshot.Roaming.Videos);
    }

    [Fact]
    public async Task DeletingCatalogEntryAlsoCleansInactiveRoamingParts()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.A, 1, "UP", "A", DateTimeOffset.UtcNow, TimeSpan.Zero)], true, token: Token);
        await f.Session.StartRoamingAsync(token: Token);
        await f.Session.PlayManualAsync(token: Token);
        await f.Session.RemoveEntryAsync(new(TestContent.A, 11), Token);
        var state = await f.Store.LoadAsync(Token);
        Assert.DoesNotContain(state.RoamingAccounts[99].Queue, q => q.Key.Cid == 11);
        await f.Session.StartRoamingAsync(token: Token);
        Assert.NotNull(f.Session.Snapshot.Current);
    }

    [Fact]
    public async Task PausingPendingScheduleCancelsItAcrossResumeAndLateCompletion()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.PlayAsync(token: Token);
        await f.Session.SetRoamingScheduleAsync(true, TimeSpan.FromHours(9), Token);
        var request = await f.Session.TryBeginScheduledRoamingAsync(99, new(2026, 10, 17), TimeSpan.FromHours(9),
            f.Session.RoamingScheduleRevision, Token);
        Assert.NotNull(request);
        await f.Session.PauseAsync(Token);
        await f.Session.CompleteScheduledRoamingAsync(request.Value, Token);
        Assert.False(f.Session.Snapshot.Roaming!.PendingStart);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        await f.Session.PlayAsync(token: Token);
        Assert.False(f.Session.Snapshot.Roaming.PendingStart);
    }

    [Fact]
    public async Task RemovedTrailingPartContinuesToAnotherSubscribedVideo()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [
            new(TestContent.A, 1, "UP", "A", DateTimeOffset.UtcNow, TimeSpan.Zero),
            new(TestContent.B, 1, "UP", "B", DateTimeOffset.UtcNow.AddDays(-10), TimeSpan.Zero)], true, token: Token);
        f.Content.MediaFailures.Add(new(TestContent.A, 12));
        await f.Session.StartRoamingAsync(token: Token);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Current?.Key.Bvid == TestContent.B ||
            f.Session.Snapshot.Playback.Intent == PlaybackIntent.Stopped);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(TestContent.B, f.Session.Snapshot.Current!.Key.Bvid);
    }

    [Fact]
    public async Task StopCancelsPendingMetadataWithoutWaitingForNetwork()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.D, 1, "UP", "D", DateTimeOffset.UtcNow, TimeSpan.Zero)], true, token: Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<VideoInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Content.CustomGetVideo = async (_, ct) => { entered.SetResult(); return await metadata.Task.WaitAsync(ct); };
        var start = f.Session.StartRoamingAsync(token: Token);
        await entered.Task.WaitAsync(Token);
        await f.Session.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        await start;
        Assert.Empty(f.Player.Loads);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
    }

    [Fact]
    public async Task PersistedRoamingRestoresOnlyAfterMatchingAccountIsBound()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.A, 1, "UP", "A", DateTimeOffset.UtcNow, TimeSpan.Zero)], true, token: Token);
        await f.Session.StartRoamingAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(22), Token);
        await f.Session.DisposeAsync();
        var player = new TestPlayer();
        await using var restored = new LibrarySession(f.Content, f.Store, player);
        await restored.InitializeAsync(Token);
        Assert.Empty(player.Loads);
        await restored.SetRoamingAccountAsync(99, Token);
        Assert.Equal(PlaybackSource.Roaming, restored.Snapshot.Source);
        Assert.Equal(TimeSpan.FromSeconds(22), player.Loads[^1].Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledBoundaryStillExecutesWhenLastPartIsUnavailable(bool previouslyFailed)
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.B, 1, "UP", "B", DateTimeOffset.UtcNow, TimeSpan.Zero)], true, token: Token);
        var first = f.Session.Snapshot.Queue[0].Id;
        f.Content.MediaFailures.Add(new(TestContent.A, 12));
        if (previouslyFailed) await f.Session.PlayAsync(f.Session.Snapshot.Queue[1].Id, Token);
        await f.Session.PlayAsync(first, Token);
        await f.Session.SetModeAsync(QueueMode.RepeatAll, Token);
        await f.Session.SetRoamingScheduleAsync(true, TimeSpan.FromHours(9), Token);
        var request = await f.Session.TryBeginScheduledRoamingAsync(99, new(2026, 10, 17), TimeSpan.FromHours(9),
            f.Session.RoamingScheduleRevision, Token);
        await f.Session.CompleteScheduledRoamingAsync(request!.Value, Token);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Source == PlaybackSource.Roaming || f.Session.Snapshot.Current?.Key.Bvid == TestContent.B);
        Assert.Equal(PlaybackSource.Roaming, f.Session.Snapshot.Source);
        Assert.False(f.Session.Snapshot.Roaming!.PendingStart);
    }

    [Fact]
    public async Task InitialMetadataNetworkFailureRetainsPlayIntentAndCanPauseThenResume()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [new(TestContent.D, 1, "UP", "D", DateTimeOffset.UtcNow, TimeSpan.Zero)], true, token: Token);
        f.Content.MetadataFailures[TestContent.D] = ContentFailureKind.Network;
        await f.Session.StartRoamingAsync(token: Token);
        Assert.True(f.Session.Snapshot.RecoveryPending);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        await f.Session.PauseAsync(Token);
        f.Content.MetadataFailures.Clear();
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.Empty(f.Player.Loads);
        await f.Session.PlayAsync(token: Token);
        Assert.Equal(TestContent.D, f.Session.Snapshot.Current!.Key.Bvid);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
