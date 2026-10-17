using WallBili.Core;

namespace WallBili.Desktop;

internal static class RoamingPresentation
{
    public static DateTime? NextScheduledAt(RoamingSnapshot snapshot, DateTime localNow)
    {
        if (!snapshot.ScheduleEnabled || snapshot.AccountId is null) return null;
        var next = localNow.Date + snapshot.ScheduleTime;
        if (next <= localNow || snapshot.LastScheduleDate == DateOnly.FromDateTime(localNow)) next = next.AddDays(1);
        return next;
    }

    public static string Failure(string? failure) => failure switch
    {
        "AuthenticationRequired" or "需要重新登录" => "需要重新登录 / 权限",
        "RateLimited" or "请求受到风控或限流，请稍后刷新" => "B 站风控，请稍后重试",
        "Network" or "网络暂不可用" => "网络错误，待重试",
        "Removed" => "视频已失效",
        "Inaccessible" => "暂不可访问",
        "Unsupported" or "MediaFailed" => "播放失败",
        null => "",
        _ => "访问失败"
    };
}
