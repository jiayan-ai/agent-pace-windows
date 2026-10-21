using System.Text.Json;
using System.Text.Json.Serialization;
namespace AgentPace.Core;
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static JsonElement? Get(this JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : null;
    public static string? Str(this JsonElement value, string key) => value.Get(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static double? Num(this JsonElement value, string key) => value.Get(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static DateTimeOffset? Unix(double? n)
    { try { return n is { } x && double.IsFinite(x) ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(x * 1000))) : null; } catch { return null; } }
    public static DateTimeOffset? Date(string? s)
    {
        if (s is null || s.Length>40) return null;
        if (DateOnly.TryParseExact(s,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var day)) return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
        if(s.Length<20 || s[10]!='T' || !System.Text.RegularExpressions.Regex.IsMatch(s,@"(Z|[+-][0-9]{2}:[0-9]{2})$"))return null;
        return DateTimeOffset.TryParse(s,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var d)?d:null;
    }
}
public sealed record UsageWindow(double? UsedPercent, int? WindowDurationMins, double? ResetsAt)
{
    [JsonIgnore] public double? Remaining => UsedPercent is { } used && double.IsFinite(used) ? Math.Clamp(100-used, 0, 100) : null;
    [JsonIgnore] public DateTimeOffset? ResetDate => Json.Unix(ResetsAt);
}
public sealed record MeteredWindow(string BucketId, string Name, string Slot, UsageWindow Window)
{
    [JsonIgnore] public string Id => $"{BucketId}/{Slot}";
    [JsonIgnore] public string Label => Name + " · " + (Window.WindowDurationMins is not { } m ? L.T("未知周期", "Unknown window") : m % 1440 == 0 ? $"{m/1440} " + L.T("天", "days") : m % 60 == 0 ? $"{m/60} " + L.T("小时", "hours") : $"{m} " + L.T("分钟", "minutes"));
}
public sealed record Snapshot(string AccountKey, DateTimeOffset FetchedAt, List<MeteredWindow> Windows, ResetCredits? Credits = null, AccountActivity? Activity = null);
public sealed record DailyBudget(string Day, string TimeZoneId, double ResetAt, int Duration, DateTimeOffset StartedAt, double StartingUsed, double Allowance, double LastUsed)
{
    [JsonIgnore] public double Consumed => Math.Max(0, LastUsed-StartingUsed);
    [JsonIgnore] public double LeftToday => Math.Min(100-LastUsed, Allowance-Consumed);
    [JsonIgnore] public double OverBudget => Math.Max(0, Consumed-Allowance);
    public static DailyBudget? Reconcile(DailyBudget? old, UsageWindow w, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (w.Remaining is not { } remaining || w.UsedPercent is not { } raw || w.ResetsAt is not { } reset || w.WindowDurationMins is not { } duration || duration <= 0 || w.ResetDate is not { } end || end <= now) return null;
        var used = Math.Clamp(raw, 0, 100);
        var day = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd");
        if (old is not null && old.Day == day && old.TimeZoneId == zone.Id && old.ResetAt > now.ToUnixTimeMilliseconds()/1000d && Math.Abs(old.ResetAt-reset) < Math.Min(60, duration*.6) && old.Duration == duration && used+.001 >= old.LastUsed) return old with { LastUsed = used };
        return new(day, zone.Id, reset, duration, now, used, remaining/Math.Max(1, (end-now).TotalDays), used);
    }
}
public sealed record SessionPace(UsageWindow Window, DateTimeOffset Now)
{
    public double? Seconds => Window.ResetDate is { } end ? (end-Now).TotalSeconds : null;
    public bool Expired => Seconds <= 0;
    public bool FinalHour => Seconds is > 0 and < 3600;
    public double? Allowance => Seconds > 0 && Window.Remaining is { } r ? r/Math.Max(1, Seconds.Value/3600) : null;
    public string Heading => FinalHour ? L.T("重置前还可以用", "Available before reset") : L.T("接下来每小时建议可用", "Suggested hourly allowance");
    public string Unit => FinalHour ? "%" : L.T("% / 小时", "% / hour");
    public string Countdown => Seconds is not { } seconds ? L.T("重置时间未知", "Reset time unknown") : seconds <= 0 ? L.T("等待重置确认", "Awaiting reset confirmation") : seconds < 60 ? L.T("不足 1 分钟", "Less than 1 minute") : seconds >= 3600 ? L.T($"{(int)Math.Ceiling(seconds/60)/60} 小时 {(int)Math.Ceiling(seconds/60)%60} 分钟", $"{(int)Math.Ceiling(seconds/60)/60}h {(int)Math.Ceiling(seconds/60)%60}m") : L.T($"{Math.Ceiling(seconds/60):0} 分钟", $"{Math.Ceiling(seconds/60):0}m");
}
public sealed class ProviderHistory
{
    public Snapshot? Snapshot { get; set; }
    public Dictionary<string,DailyBudget> Budgets { get; set; } = [];
    public string SelectedId { get; set; } = "";
    public AccountActivity? Activity { get; set; }
    public DateTimeOffset? ActivityFetchedAt { get; set; }
    public void Ingest(Snapshot current)
    {
        if (Snapshot?.AccountKey != current.AccountKey) { Budgets.Clear(); Activity = null; ActivityFetchedAt = null; }
        if (current.Activity?.DailyUsageBuckets is not null) { Activity = current.Activity.Recent(current.FetchedAt); ActivityFetchedAt = current.FetchedAt; }
        Snapshot = current;
        var next = new Dictionary<string,DailyBudget>();
        foreach (var w in current.Windows) { var key = current.AccountKey+"/"+w.Id; Budgets.TryGetValue(key,out var previous); if (DailyBudget.Reconcile(previous,w.Window,current.FetchedAt) is { } b) next[key] = b; }
        Budgets = next;
        if (!current.Windows.Any(w=>w.Id==SelectedId)) SelectedId = current.Windows.FirstOrDefault()?.Id ?? "";
    }
}
public sealed class History
{
    public string SelectedProvider { get; set; } = "codex";
    public Dictionary<string,ProviderHistory> Providers { get; set; } = [];
    public ProviderHistory For(string provider) { if (!Providers.TryGetValue(provider,out var h)) Providers[provider] = h = new(); return h; }
}
public static class UsageParser
{
    public static List<MeteredWindow> Codex(JsonElement response)
    {
        var result = new List<MeteredWindow>();
        var multiple = response.Get("rateLimitsByLimitId");
        if (multiple is { ValueKind: JsonValueKind.Object } map && map.EnumerateObject().Any()) foreach(var entry in map.EnumerateObject()) Add(entry.Name,entry.Value);
        else if (response.Get("rateLimits") is { ValueKind: JsonValueKind.Object } legacy) Add(legacy.Str("limitId") ?? "codex",legacy);
        void Add(string id,JsonElement bucket) { foreach(var slot in new[]{"primary","secondary"}) if (bucket.Get(slot) is { ValueKind: JsonValueKind.Object } w) result.Add(new(id,bucket.Str("limitName") ?? (id=="codex"?"Codex":id),slot,new(w.Num("usedPercent"),w.Num("windowDurationMins") is { } m && m>0 && m<=int.MaxValue ? (int)m:null,w.Num("resetsAt")))); }
        return Sort(result);
    }
    public static List<MeteredWindow> Sort(IEnumerable<MeteredWindow> list) => list.OrderBy(w=>w.BucketId is "codex" or "claude" ? 0:1).ThenBy(w=>w.BucketId,StringComparer.Ordinal).ThenByDescending(w=>w.Window.WindowDurationMins).ThenBy(w=>w.Id,StringComparer.Ordinal).ToList();
    public static Snapshot Claude(JsonElement reply,DateTimeOffset now)
    {
        var status = reply.Num("status");
        if(status==429) throw new PaceException("Claude 查询暂时受限，请稍后重试。","Claude is rate limited. Please retry later.",false,(int)Math.Clamp(reply.Num("retryAfter")??600,300,3600));
        if(status==409) throw new PaceException("请选择 Claude 组织后同步。","Select a Claude organization, then sync.",true);
        if(status is 401 or 403) throw new PaceException("请在连接窗口完成 Claude 登录或验证，再同步额度。","Complete Claude sign-in or verification in the connection window, then sync.",true);
        if(status!=200) throw PaceException.Network();
        var org=reply.Str("organization");
        if(!Guid.TryParse(org,out _) || reply.Get("organizations") is not {ValueKind:JsonValueKind.Array} organizations || !organizations.EnumerateArray().Any(o=>string.Equals(o.Str("uuid"),org,StringComparison.OrdinalIgnoreCase)) || reply.Get("usage") is not {} usage) throw PaceException.Invalid();
        var windows=new List<MeteredWindow>();
        foreach(var (key,id,name,slot,mins) in new[]{("five_hour","claude","Claude","secondary",300),("seven_day","claude","Claude","primary",10080),("seven_day_fable","claude_fable","Claude Fable","primary",10080),("seven_day_sonnet","claude_sonnet","Claude Sonnet","primary",10080),("seven_day_opus","claude_opus","Claude Opus","primary",10080)})
            if(usage.Get(key) is {} w && w.Num("utilization") is {} used && used>=0) windows.Add(new(id,name,slot,new(used,mins,Json.Date(w.Str("resets_at"))?.ToUnixTimeMilliseconds()/1000d)));
        if(windows.Count==0) throw new PaceException("Claude 尚未返回订阅额度。","Claude has not returned subscription quota.");
        return new(Hash("claude-web/"+org!.ToLowerInvariant()),now,Sort(windows));
    }
    public static string Hash(string text)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
