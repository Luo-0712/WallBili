using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class RecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ContentFailureKind.Network)]
    [InlineData(ContentFailureKind.RateLimited)]
    public async Task TransientFailureBacksOffWithoutLosingIdentityProgressOrPoisoningQueue(ContentFailureKind failure)
    {
        var clock = new Clock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(19), Token);
        var current = f.Session.Snapshot.Playback.CurrentId;
        f.Content.ResolveFailures[f.Session.Snapshot.Current!.Key] = failure;
        await f.Session.ReloadAsync(Token);
        Assert.True(f.Session.Snapshot.RecoveryPending);
        Assert.False(f.Session.Snapshot.OutputActive);
        var count = f.Content.Resolved.Count;
        await f.Session.RetryRecoveryAsync(token: Token);
        Assert.Equal(count, f.Content.Resolved.Count); // No retry before its deadline.
        for (var i = 0; i < 9; i++)
        {
            clock.Now = f.Session.Snapshot.NextRecoveryAt!.Value;
            await f.Session.RetryRecoveryAsync(token: Token);
            Assert.Equal(current, f.Session.Snapshot.Playback.CurrentId);
            Assert.Equal(19, f.Session.Snapshot.Playback.PositionSeconds);
            Assert.InRange((f.Session.Snapshot.NextRecoveryAt!.Value - clock.Now).TotalSeconds, 2, 60);
        }
        Assert.All(f.Session.Snapshot.Entries, e => Assert.NotEqual(EntryAvailability.Failed, e.Availability));
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        f.Content.ResolveFailures.Clear();
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.False(f.Session.Snapshot.RecoveryPending);
        Assert.True(f.Session.Snapshot.OutputActive);
        Assert.Equal(TimeSpan.FromSeconds(19), f.Player.Loads[^1].Position);
        Assert.Equal(current, f.Session.Snapshot.Playback.CurrentId);
    }

    [Fact]
    public async Task UserStopOrDeleteCancelsPendingRecoveryAndLateEventsCannotRestartPlayback()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        var generation = f.Player.Generation;
        f.Player.End("disconnect");
        await WaitAsync(() => f.Session.Snapshot.RecoveryPending);
        await f.Session.StopAsync(Token);
        var loads = f.Player.Loads.Count;
        f.Player.End("error", generation);
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        await f.Session.CheckpointAsync(Token);
        Assert.Equal(loads, f.Player.Loads.Count);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
        Assert.False(f.Session.Snapshot.RecoveryPending);
        await f.Session.PlayAsync(f.Session.Snapshot.Queue[^1].Id, Token);
        f.Player.End("disconnect");
        await WaitAsync(() => f.Session.Snapshot.RecoveryPending);
        await f.Session.RemoveQueueAsync(f.Session.Snapshot.Playback.CurrentId!.Value, Token);
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.False(f.Session.Snapshot.RecoveryPending);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SleepAndOutputReplacementRetainCurrentIntentAndUseFreshResources(bool paused)
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(27), Token);
        if (paused) await f.Session.PauseAsync(Token);
        var current = f.Session.Snapshot.Playback.CurrentId;
        await f.Session.SuspendAsync(Token);
        var loads = f.Player.Loads.Count;
        await f.Session.RebindOutputAsync(Token);
        Assert.Equal(loads, f.Player.Loads.Count);
        Assert.False(f.Session.Snapshot.OutputActive);
        await f.Session.ResumeAsync(Token);
        await f.Session.RebindOutputAsync(Token);
        Assert.Equal(loads + 2, f.Player.Loads.Count);
        Assert.Equal(current, f.Session.Snapshot.Playback.CurrentId);
        Assert.Equal(TimeSpan.FromSeconds(27), f.Player.Loads[^1].Position);
        Assert.Equal(paused, f.Player.Loads[^1].Paused);
        Assert.Equal(3, f.Content.Resolved.Count);
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        await f.Session.SuspendAsync(Token);
        await f.Session.StopAsync(Token);
        await f.Session.ResumeAsync(Token);
        Assert.Equal(loads + 2, f.Player.Loads.Count);
    }

    [Fact]
    public async Task StalledClockRecoversButPausedOrAdvancingClockDoesNot()
    {
        var clock = new Clock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        clock.Now += TimeSpan.FromSeconds(40);
        f.Player.Position = TimeSpan.FromSeconds(12);
        await f.Session.CheckpointAsync(Token);
        clock.Now += TimeSpan.FromSeconds(40);
        await f.Session.CheckpointAsync(Token);
        Assert.False(f.Session.Snapshot.RecoveryPending);
        clock.Now += TimeSpan.FromSeconds(6);
        await f.Session.CheckpointAsync(Token);
        Assert.True(f.Session.Snapshot.RecoveryPending);
        await f.Session.PauseAsync(Token); // Pausing while offline must be honored by the retry.
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.True(f.Player.Paused);
        clock.Now += TimeSpan.FromHours(2);
        await f.Session.CheckpointAsync(Token);
        Assert.False(f.Session.Snapshot.RecoveryPending);
        Assert.Equal(12, f.Session.Snapshot.Playback.PositionSeconds);
    }

    [Fact]
    public async Task OfflineStartupRestoresSavedIntentAndRetriesWhenNetworkArrives()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(16), Token);
        await f.Session.DisposeAsync();
        f.Content.ResolveFailures[f.Session.Snapshot.Current!.Key] = ContentFailureKind.Network;
        var player = new TestPlayer();
        await using var restored = new LibrarySession(f.Content, f.Store, player);
        await restored.InitializeAsync(Token);
        Assert.True(restored.Snapshot.RecoveryPending);
        Assert.Equal(PlaybackIntent.Playing, restored.Snapshot.Playback.Intent);
        Assert.Empty(player.Loads);
        f.Content.ResolveFailures.Clear();
        await restored.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.Equal(TimeSpan.FromSeconds(16), Assert.Single(player.Loads).Position);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
