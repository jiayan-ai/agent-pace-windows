using System.Globalization;
namespace AgentPace.Core;

public sealed record ResetCredit(string Id, string ResetType, string Status, double? GrantedAt, double? ExpiresAt, string? Title, string? Description)
{
    public DateTimeOffset? Expiry => Json.Unix(ExpiresAt);
}
public sealed record ResetCredits([property: System.Text.Json.Serialization.JsonRequired] int AvailableCount, List<ResetCredit>? Credits)
{
    public IEnumerable<ResetCredit> Usable(DateTimeOffset now) => (Credits ?? []).Where(c => c.Status == "available" && c.ResetType == "codexRateLimits" && (c.ExpiresAt is null || c.Expiry > now)).OrderBy(c => c.Expiry ?? DateTimeOffset.MaxValue);
}
public sealed record DailyActivity(string StartDate, [property: System.Text.Json.Serialization.JsonRequired] double Tokens);
public sealed record ActivitySummary(double? LifetimeTokens, double? PeakDailyTokens)
{
    public ActivitySummary Clean() => new(Valid(LifetimeTokens), Valid(PeakDailyTokens));
    static double? Valid(double? value) => value is { } n && double.IsFinite(n) && n >= 0 && n <= 9_007_199_254_740_991 && n == Math.Truncate(n) ? n : null;
}
public sealed record AccountActivity(List<DailyActivity>? DailyUsageBuckets, ActivitySummary? Summary = null)
{
    public static DateOnly? Date(string? text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    // Keep today's UTC bucket for the token card. Pattern analysis still uses
    // completed days only, so an incomplete day cannot distort the trend.
    public AccountActivity Clean(DateTimeOffset now, bool includeToday = true)
    {
        if (DailyUsageBuckets is null)
            return this with { Summary = Summary?.Clean() };
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new(DailyUsageBuckets.Where(x => x is not null && double.IsFinite(x.Tokens) && x.Tokens >= 0 && x.Tokens <= 9_007_199_254_740_991 && x.Tokens == Math.Truncate(x.Tokens) && Date(x.StartDate) is { } d && d >= today.AddDays(-30) && (includeToday ? d <= today : d < today)).GroupBy(x => x.StartDate).Select(g => g.MaxBy(x => x.Tokens)!).OrderBy(x => x.StartDate, StringComparer.Ordinal).ToList(), Summary?.Clean());
    }
    public AccountActivity Recent(DateTimeOffset now) => Clean(now, includeToday: false);
    public double? TodayTokens(DateTimeOffset now) => Clean(now).DailyUsageBuckets?.FirstOrDefault(d => d.StartDate == now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))?.Tokens;
    public List<DailyActivity>? Last30Days(DateTimeOffset now) => Clean(now).DailyUsageBuckets?.Where(d => Date(d.StartDate) >= DateOnly.FromDateTime(now.UtcDateTime).AddDays(-29)).ToList();
    public double? Last30Tokens(DateTimeOffset now)
    {
        if (Last30Days(now) is not { Count: > 0 } days) return null;
        double total = 0;
        foreach (var day in days)
        {
            if (day.Tokens > 9_007_199_254_740_991 - total) return null;
            total += day.Tokens;
        }
        return total;
    }
}
public sealed class UsagePattern
{
    public List<DailyActivity> Days
    {
        get;
    }
    public bool Sufficient => Days.Count >= 14;
    public int ActiveDays => Days.Count(d => d.Tokens > 0);
    public double? RecentRatio
    {
        get;
    }
    public DayOfWeek? BusyWeekday
    {
        get;
    }
    public bool HighDemandSoon
    {
        get;
    }
    public UsagePattern(AccountActivity? activity, DateTimeOffset now)
    {
        Days = activity?.Recent(now).DailyUsageBuckets ?? [];
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var recent = Days.Where(d => AccountActivity.Date(d.StartDate) >= today.AddDays(-7)).ToList();
        var earlier = Days.Except(recent).ToList();
        if (recent.Count >= 4 && earlier.Count >= 7 && earlier.Average(d => d.Tokens) > 0)
            RecentRatio = recent.Average(d => d.Tokens) / earlier.Average(d => d.Tokens);
        var mean = Days.Count > 0 ? Days.Average(d => d.Tokens) : 0;
        var ranked = Days.GroupBy(d => AccountActivity.Date(d.StartDate)!.Value.DayOfWeek).Where(g => g.Count() >= 3).Select(g => new { Day = g.Key, Score = (g.Sum(d => d.Tokens) + mean * 2) / (g.Count() + 2) }).Where(g => g.Score > mean * 1.2).OrderByDescending(g => g.Score).ThenBy(g => g.Day).FirstOrDefault();
        if (Sufficient)
            BusyWeekday = ranked?.Day;
        var offset = BusyWeekday is { } busy ? ((int)busy - (int)today.DayOfWeek + 7) % 7 : 7;
        HighDemandSoon = Sufficient && (offset <= 1 || RecentRatio >= 1.35);
    }
}
public sealed record ResetAdvice(string Kind, string Title, string Reason, string? PreferredCreditId = null)
{
    public static ResetAdvice Evaluate(Snapshot? snapshot, UsagePattern pattern, bool historyFresh, bool stale, DateTimeOffset now)
    {
        if (stale || snapshot is null)
            return new("refresh", L.T("先同步最新额度", "Sync usage first"), L.T("上次记录不能用于决定是否消耗 reset。", "Last-known usage cannot determine whether to spend a reset."));
        if (snapshot.Credits is not { } credits || credits.AvailableCount < 0)
            return new("unavailable", L.T("Reset 数量暂不可用", "Reset count unavailable"), L.T("接口未返回数量，不代表没有 reset。", "A missing count does not mean you have no resets."));
        if (credits.AvailableCount == 0)
            return new("none", L.T("目前没有可用 Reset", "No resets available"), L.T("等待自然重置，或回到 Codex 查看额度。", "Wait for a natural reset, or check your account in Codex."));
        if ((credits.Credits ?? []).Any(c => c.Status == "available" && c.Expiry <= now))
            return new("refresh", L.T("Reset 有效期已变化", "Reset expiry has changed"), L.T("有记录中的券已到期，请刷新确认。", "A recorded credit expired. Refresh to confirm availability."));
        var windows = snapshot.Windows.Where(w => w.BucketId == "codex").ToList();
        if (windows.Count == 0 || windows.Any(w => w.Window.Remaining is null || w.Window.ResetDate is null || w.Window.ResetDate <= now))
            return new("refresh", L.T("等待有效重置时间", "Awaiting a valid reset time"), L.T("窗口已过期或信息不完整，请刷新。", "A window expired or is incomplete. Refresh first."));
        var pressure = windows.Where(w => w.Window.Remaining <= 10).ToList();
        var lasting = pressure.Where(w => w.Window.ResetDate > now.AddHours(6)).ToList();
        var first = credits.Usable(now).FirstOrDefault();
        if (pressure.Count > 0 && lasting.Count == 0)
            return new("wait", L.T("优先等自然重置", "Prefer the natural reset"), L.T("接近耗尽的窗口将在 6 小时内恢复；工作可以等就先保留。", "The pressured windows reset within six hours. Keep the credit if work can wait."));
        if (lasting.FirstOrDefault(w => w.Window.Remaining == 0) is { } empty)
            return new("consider", L.T("继续工作时可考虑 Reset", "Consider a reset to continue working"), L.T($"{empty.Label} 已耗尽，重置仍超过 6 小时；需要继续时优先用最早到期的券。", $"{empty.Label} is depleted and resets in over six hours. If work must continue, use the earliest-expiring credit first."), first?.Id);
        if (!historyFresh || !pattern.Sufficient)
            return new("learning", L.T("先保留，历史仍需补足", "Keep resets while history builds"), L.T($"近 30 天有 {pattern.Days.Count} 天记录；至少需要 14 天和正常历史同步。", $"{pattern.Days.Count} recorded days in the last 30; patterns need at least 14 days and fresh history."));
        if (lasting.Count > 0 && pattern.HighDemandSoon)
            return new("consider", L.T("集中工作前考虑 Reset", "Reassess before focused work"), L.T("有窗口剩余不超过 10%，近期可能较忙；真正需要继续时再使用。", "A window has 10% or less remaining and demand may rise. Use a reset only when you need to continue."), first?.Id);
        if (first?.Expiry <= now.AddDays(3))
            return new("reserve", L.T("优先安排快到期的 Reset", "Prioritize an expiring reset"), L.T("有券将在 3 天内到期。需要时优先用它，无需刻意消耗额度。", "A credit expires within three days. Prioritize it if needed; do not burn quota just to spend it."), first.Id);
        if (pattern.HighDemandSoon)
            return new("reserve", L.T("留给高用量时段", "Keep it for higher demand"), L.T("近期可能较忙，但没有需要立即重置的窗口。", "Recent patterns suggest higher demand, but no window needs an immediate reset."), first?.Id);
        return new("wait", L.T("暂时保留 Reset", "Keep resets for now"), L.T("当前额度与近月模式没有显示立即重置的必要。", "Current quota and recent patterns do not indicate an immediate need."));
    }
}
