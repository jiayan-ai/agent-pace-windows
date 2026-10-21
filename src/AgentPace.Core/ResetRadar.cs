using System.Text.Json;
using System.Text.Json.Serialization;
namespace AgentPace.Core;

public sealed record PublicSource(string Id, string Status, [property: JsonPropertyName("checked_at")] string? CheckedAt);
public sealed record ResetEvent(string Id, string Type, string Status, [property: JsonPropertyName("announced_at")] string? AnnouncedAt, [property: JsonPropertyName("effective_at")] string? EffectiveAt, [property: JsonPropertyName("time_precision")] string? TimePrecision, [property: JsonPropertyName("time_basis")] string? TimeBasis, [property: JsonPropertyName("source_url")] string? SourceUrl, string? Scope)
{
    [JsonIgnore] public DateTimeOffset? Date => Json.Date(AnnouncedAt) ?? Json.Date(EffectiveAt);
    [JsonIgnore] public bool Precise => (TimePrecision is "minute" or "second") && (AnnouncedAt ?? EffectiveAt ?? "").Contains('T');
    [JsonIgnore] public bool Usable => !string.IsNullOrWhiteSpace(Id) && (Type is "global_reset" or "banked_reset") && (Status is "confirmed" or "recorded" or "planned") && Date is not null && Evidence(SourceUrl) is not null;
    [JsonIgnore] public string Label => Type == "global_reset" ? L.T("全局 Reset", "Global reset") : L.T("Reset 券发放", "Reset credit grant");
    [JsonIgnore] public string Verification => Status == "confirmed" ? L.T("追踪站已核验", "Tracker verified") : Status == "planned" ? L.T("预告 · 待执行", "Planned · Not yet confirmed") : L.T("社区收录 · 未独立核验", "Community record · Not independently verified");
    public static Uri? Evidence(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo != "")
            return null;
        if (new[] { "openai.com", "help.openai.com", "learn.chatgpt.com" }.Contains(u.Host))
            return u;
        if (!new[] { "x.com", "www.x.com", "twitter.com", "www.twitter.com" }.Contains(u.Host))
            return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0].Equals("thsottiaux", StringComparison.OrdinalIgnoreCase) && parts[1] == "status" && parts[2].All(char.IsAsciiDigit) ? u : null;
    }
}
public sealed record RadarFeed(string Status, string GeneratedAt, string? ResetArchiveSyncedAt, ResetEvent? LatestGlobalReset, ResetEvent? LatestBankedReset, List<ResetEvent>? Events, List<PublicSource>? Sources)
{
    [JsonIgnore] public List<ResetEvent> Records => (Events ?? []).Concat(new[] { LatestGlobalReset, LatestBankedReset }.OfType<ResetEvent>()).Where(e => e is not null && e.Usable).DistinctBy(e => e.Id).OrderByDescending(e => e.Date).ToList();
}
public sealed record RadarSnapshot(RadarFeed Feed, DateTimeOffset FetchedAt)
{
    static bool Recent(DateTimeOffset? date, DateTimeOffset now) => date is { } d && (now - d).TotalMinutes is >= -5 and <= 30;
    public bool Fresh(DateTimeOffset now) => new[] { "confirmed_today", "recorded_today", "no_verified_reset" }.Contains(Feed.Status) && Recent(FetchedAt, now) && Recent(Json.Date(Feed.GeneratedAt), now) && Recent(Json.Date(Feed.ResetArchiveSyncedAt), now) && Feed.Sources?.Any(s => s is not null && s.Id == "codex-resets" && s.Status == "online" && Recent(Json.Date(s.CheckedAt), now)) == true;
    public (string Title, string Detail) Assessment(DateTimeOffset now, bool failed = false, TimeZoneInfo? zone = null)
    {
        if (failed || !Fresh(now))
            return (L.T("Reset 动态待确认", "Reset updates unconfirmed"), L.T("来源过期或连接失败；保留上次记录。", "The source is stale or unreachable; the last record is retained."));
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var events = Feed.Records.Where(e => e.Precise && e.Date <= now && TimeZoneInfo.ConvertTime(e.Date!.Value, zone).Date == today).ToList();
        if (events.Any(e => e.Type == "global_reset" && e.Status != "planned"))
            return (L.T("今日有全局 Reset 记录", "Global reset recorded today"), L.T("公告不等于账户到账，请以实际额度为准。", "An announcement is not account delivery. Check your actual quota."));
        if (Feed.Records.Any(e => e.Status == "planned" && Json.Date(e.AnnouncedAt) is { } d && d <= now && d > now.AddDays(-1)))
            return (L.T("有新的 Reset 预告", "New reset announcement"), L.T("尚未确认执行，请查看原帖。", "Execution is unconfirmed. Check the original source."));
        if (events.Any(e => e.Type == "banked_reset" && e.Status != "planned"))
            return (L.T("今日有 Reset 券发放记录", "Reset credit grant recorded today"), L.T("发券不等于额度自动恢复。", "A credit grant does not automatically restore quota."));
        return (L.T("今日尚无全局 Reset 记录", "No global reset recorded today"), L.T("暂未收录不代表今天不会 Reset。", "No record does not mean no reset will happen."));
    }
}
public static class RadarClient
{
    public static async Task<RadarSnapshot> Fetch(CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentPaceWindows/1.0");
        using var response = await client.GetAsync("https://codexreset.dev/api/status?lang=" + (L.Chinese ? "zh" : "en"), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1_048_576)
            throw PaceException.Invalid();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + n > 1_048_576)
                throw PaceException.Invalid();
            buffer.Write(bytes, 0, n);
        }
        var feed = JsonSerializer.Deserialize<RadarFeed>(buffer.ToArray(), Json.Options) ?? throw PaceException.Invalid();
        if (Json.Date(feed.GeneratedAt) is null)
            throw PaceException.Invalid();
        return new(feed, DateTimeOffset.UtcNow);
    }
}
