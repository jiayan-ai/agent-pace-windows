using System.Text.Json.Serialization;
namespace AgentPace.Core;

// A reset is a conditional future refill, never a second balance on the server.
// Keep this ledger separate from the ordinary daily budget and hourly deadline.
public sealed record ResetPlanBudget(string Day, string TimeZoneId, DateTimeOffset StartedAt,
    double ExpiryAt, double NaturalResetAt, int Duration, List<string> CreditIds,
    double StartingRemaining, double StartingTotal, int PlanningDays, double Allowance,
    double Consumed, double LastUsed, double LastResetAt, List<string>? RemainingCreditIds = null)
{
    [JsonIgnore] public DateTimeOffset? Expiry => Json.Unix(ExpiryAt);
    [JsonIgnore] public double OverBudget => Math.Max(0, Consumed - Allowance);
    public double? Capacity(Snapshot snapshot, MeteredWindow selected, DateTimeOffset now)
    {
        if (selected.BucketId != "codex" || selected.Window.Remaining is not { } remaining ||
            selected.Window.ResetDate is not { } reset || reset <= now || selected.Window.WindowDurationMins != Duration ||
            snapshot.Credits is not { AvailableCount: >= 0 } credits || credits.Credits is null)
            return null;
        var valid = ValidCredits(credits, now).Where(c => (RemainingCreditIds ?? CreditIds).Contains(c.Id) && c.ExpiresAt == ExpiryAt && c.Expiry < reset).ToList();
        return remaining + Math.Min(credits.AvailableCount, valid.Count) * 100d;
    }
    public double? LeftToday(Snapshot snapshot, MeteredWindow selected, DateTimeOffset now) =>
        Capacity(snapshot, selected, now) is { } cap ? Math.Min(Allowance - Consumed, cap) : null;
    public static ResetPlanBudget? Reconcile(ResetPlanBudget? old, Snapshot snapshot, MeteredWindow selected, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var window = selected.Window;
        if (selected.BucketId != "codex" || window.Remaining is not { } remaining || window.UsedPercent is not { } raw ||
            window.WindowDurationMins is not > 0 || window.ResetsAt is not { } reset || window.ResetDate is not { } end || end <= now)
            return old;
        var used = Math.Clamp(raw, 0, 100);
        var day = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd");
        if (old is not null && old.Day == day && old.TimeZoneId == zone.Id && old.Duration == window.WindowDurationMins)
        {
            var resetAdvanced = reset - old.LastResetAt >= Math.Min(60, old.Duration * .6);
            var confirmedReset = resetAdvanced && (Json.Unix(old.LastResetAt) <= now || used + .001 < old.LastUsed);
            var remainingIds = old.RemainingCreditIds ?? old.CreditIds;
            if (snapshot.Credits is { AvailableCount: >= 0, Credits: not null } currentCredits)
                remainingIds = ValidCredits(currentCredits, now).Where(c => remainingIds.Contains(c.Id) && c.ExpiresAt == old.ExpiryAt && c.Expiry < end)
                    .Select(c => c.Id).Order(StringComparer.Ordinal).Take(currentCredits.AvailableCount).ToList();
            return old with { Consumed = old.Consumed + (confirmedReset ? used : Math.Max(0, used - old.LastUsed)), LastUsed = used, LastResetAt = reset, RemainingCreditIds = remainingIds };
        }
        if (snapshot.Credits is null || snapshot.Credits.Credits is null)
            return old;
        if (snapshot.Credits is not { AvailableCount: > 0 } credits)
            return null;
        var eligible = ValidCredits(credits, now).Where(c => c.Expiry < end).ToList();
        if (eligible.Count == 0)
            return null;
        var expiry = eligible.Min(c => c.Expiry)!.Value;
        var batch = eligible.Where(c => c.Expiry == expiry).ToList();
        if (batch.Count > credits.AvailableCount)
            return null;
        var days = Math.Max(1, (TimeZoneInfo.ConvertTime(expiry, zone).Date - TimeZoneInfo.ConvertTime(now, zone).Date).Days + 1);
        var total = remaining + batch.Count * 100d;
        return new(day, zone.Id, now, expiry.ToUnixTimeMilliseconds() / 1000d, reset, window.WindowDurationMins.Value,
            batch.Select(c => c.Id).Order(StringComparer.Ordinal).ToList(), remaining, total, days, total / days, 0, used, reset);
    }
    static List<ResetCredit> ValidCredits(ResetCredits credits, DateTimeOffset now) => (credits.Credits ?? [])
        .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id))
        .GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())
        .Where(c => c.Status == "available" && c.ResetType == "codexRateLimits" && c.ExpiresAt <= 253402300799 && c.Expiry > now).ToList();
}
