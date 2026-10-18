using WallBili.Core;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class RoamingServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RefreshIsCoalescedAndPauseDuringRefreshPreventsLateStart()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        var discovery = new DiscoveryStub();
        await using var service = new RoamingService(f.Session, discovery);
        await service.SetAccountAsync(99, Token);
        await service.SaveSubscriptionsAsync([new(1, "UP")], Token);
        discovery.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = service.StartAsync(Token);
        await discovery.Entered.Task.WaitAsync(Token);
        var refresh = service.RefreshAsync(Token);
        await f.Session.PauseAsync(Token);
        discovery.Pending.SetResult(discovery.Page());
        await Task.WhenAll(start, refresh);
        Assert.Equal(1, discovery.Calls);
        Assert.Equal(PlaybackSource.Manual, f.Session.Snapshot.Source);
        Assert.Empty(f.Player.Loads);
        Assert.Equal(2, f.Session.Snapshot.Roaming!.Videos.Count);
    }

    [Fact]
    public async Task ScheduledStartWaitsForConsecutivePartsAndOnlyTriggersOnce()
    {
        var clock = new RoamingClock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        var discovery = new DiscoveryStub();
        await using var service = new RoamingService(f.Session, discovery, clock);
        await service.SetAccountAsync(99, Token);
        await service.SaveSubscriptionsAsync([new(1, "UP")], Token);
        await service.SetScheduleAsync(true, TimeSpan.FromHours(9), Token);
        await f.Session.PlayAsync(token: Token);
        clock.Now = clock.Now.AddMinutes(2);
        await service.TickAsync(Token);
        Assert.True(f.Session.Snapshot.Roaming!.PendingStart);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Current?.Key.Cid == 12);
        Assert.Equal(PlaybackSource.Manual, f.Session.Snapshot.Source);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Source == PlaybackSource.Roaming);
        var loads = f.Player.Loads.Count;
        await service.TickAsync(Token);
        Assert.Equal(loads, f.Player.Loads.Count);
        await f.Session.PlayManualAsync(token: Token);
        Assert.Equal(TestContent.B, f.Session.Snapshot.Current!.Key.Bvid);
        Assert.Equal(TimeSpan.Zero, f.Player.Loads[^1].Position);
    }

    [Fact]
    public async Task SleepClockResetDoesNotCatchUpMissedSchedule()
    {
        var clock = new RoamingClock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        await using var service = new RoamingService(f.Session, new DiscoveryStub(), clock);
        await service.SetAccountAsync(99, Token);
        await service.SaveSubscriptionsAsync([new(1, "UP")], Token);
        await service.SetScheduleAsync(true, TimeSpan.FromHours(9), Token);
        clock.Now = clock.Now.AddHours(2);
        service.ResetClock();
        await service.TickAsync(Token);
        Assert.Empty(f.Player.Loads);
        Assert.False(f.Session.Snapshot.Roaming!.PendingStart);
    }

    [Fact]
    public async Task FailedPaginationRescansFrontAfterRemoteDeletionWithoutMissingNewVideo()
    {
        var clock = new RoamingClock();
        await using var f = new LibraryFixture(clock); await f.SeedAsync();
        var discovery = new DiscoveryStub();
        await using var service = new RoamingService(f.Session, discovery, clock);
        await service.SetAccountAsync(99, Token);
        await service.SaveSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.MergeRoamingPageAsync(99, 1, [], true, scanStartedAt: clock.Now.AddDays(-2), token: Token);
        var videos = Enumerable.Range(1, 70).Select(i => new CreatorVideo($"BV{i:D10}", 1, "UP", "N" + i,
            clock.Now.AddSeconds(-i), TimeSpan.FromMinutes(1))).ToList();
        var failing = true;
        discovery.GetPage = page => failing && page == 3
            ? throw new ContentAccessException(ContentFailureKind.Network, "fixture")
            : new(1, page, 30, videos.Count, videos.Skip((page - 1) * 30).Take(30).ToArray());
        await service.RefreshAsync(Token);
        Assert.Equal(60, f.Session.Snapshot.Roaming!.Videos.Count);
        Assert.Equal(3, f.Session.Snapshot.Roaming.Sync[1].NextPage);
        Assert.Null(f.Session.Snapshot.Roaming.LastRefreshDate);
        videos.RemoveAt(0); failing = false;
        await service.RefreshAsync(Token);
        Assert.Contains(f.Session.Snapshot.Roaming.Videos, v => v.Video.Title == "N61");
        Assert.Equal(70, f.Session.Snapshot.Roaming.Videos.Count);
        Assert.NotNull(f.Session.Snapshot.Roaming.LastRefreshDate);
    }

    [Fact]
    public async Task ClosedOrReplacedScheduleCannotArmAnOldOccurrence()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.SetRoamingScheduleAsync(true, TimeSpan.FromHours(9), Token);
        var revision = f.Session.RoamingScheduleRevision;
        await f.Session.SetRoamingScheduleAsync(false, TimeSpan.FromHours(9), Token);
        Assert.Null(await f.Session.TryBeginScheduledRoamingAsync(99, new(2026, 10, 17), TimeSpan.FromHours(9), revision, Token));
        await f.Session.SetRoamingAccountAsync(100, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "Other")], Token);
        Assert.Null(await f.Session.TryBeginScheduledRoamingAsync(99, new(2026, 10, 17), TimeSpan.FromHours(9), revision, Token));
        Assert.False(f.Session.Snapshot.Roaming!.PendingStart);
        Assert.Null(f.Session.Snapshot.Roaming.LastScheduleDate);
    }

    [Fact]
    public async Task ClearingSubscriptionsInvalidatesAnAlreadyObservedSchedule()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetRoamingAccountAsync(99, Token);
        await f.Session.SetSubscriptionsAsync([new(1, "UP")], Token);
        await f.Session.SetRoamingScheduleAsync(true, TimeSpan.FromHours(9), Token);
        await f.Session.PlayAsync(token: Token);
        var revision = f.Session.RoamingScheduleRevision;
        await f.Session.SetSubscriptionsAsync([], Token);
        Assert.Null(await f.Session.TryBeginScheduledRoamingAsync(99, new(2026, 10, 17), TimeSpan.FromHours(9), revision, Token));
        Assert.Equal(PlaybackSource.Manual, f.Session.Snapshot.Source);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

internal sealed class RoamingClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 8, 59, 0, TimeSpan.FromHours(8));
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");
}

internal sealed class DiscoveryStub : IContentDiscoveryClient
{
    public int Calls;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<CreatorVideosPage>? Pending { get; set; }
    public Func<int, CreatorVideosPage>? GetPage { get; set; }
    public CreatorVideosPage Page() => new(1, 1, 30, 2, [
        new(TestContent.A, 1, "UP", "New", new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(120)),
        new(TestContent.B, 1, "UP", "Old", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(60))]);
    public Task<FollowingsPage> GetFollowingsAsync(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FollowingsPage(page, pageSize, 1, [new(1, "UP")]));
    public async Task<CreatorVideosPage> GetCreatorVideosAsync(long uploaderId, int page = 1, int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls); Entered.TrySetResult();
        return Pending is { } pending ? await pending.Task.WaitAsync(cancellationToken) : GetPage?.Invoke(page) ?? Page();
    }
}
