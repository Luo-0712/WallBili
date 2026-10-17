using WallBili.Core;
using Xunit;

namespace WallBili.Desktop.Tests;

public sealed class RoamingPresentationTests
{
    [Fact]
    public void AlreadyTriggeredTodayShowsTomorrowEvenIfTheClockMovesBack()
    {
        var snapshot = RoamingSnapshot.Empty with { AccountId = 42, ScheduleEnabled = true,
            ScheduleTime = TimeSpan.FromHours(9), LastScheduleDate = new(2026, 10, 17) };

        var next = RoamingPresentation.NextScheduledAt(snapshot, new(2026, 10, 17, 8, 0, 0));

        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), next);
    }

    [Fact]
    public void CachedRefreshFailuresStillDistinguishRateLimitsFromAnExpiredLogin()
    {
        Assert.Contains("风控", RoamingPresentation.Failure("请求受到风控或限流，请稍后刷新"));
        Assert.Contains("登录", RoamingPresentation.Failure("需要重新登录"));
        Assert.Contains("网络", RoamingPresentation.Failure("网络暂不可用"));
    }
}
