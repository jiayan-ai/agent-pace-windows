using System.Diagnostics;
using System.Text.Json;
using AgentPace.Core;
if (args.Contains("--fake-codex")) { await Fake(); return; }
var passed = 0;
var failed = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); failed++; } }
async Task AsyncCheck(string name, Func<Task> test) { try { await test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); failed++; } }
void Assert(bool value, string? message = null) { if (!value) throw new Exception(message ?? "Assertion failed"); }
void Near(double? actual, double expected) { Assert(actual.HasValue && Math.Abs(actual.Value - expected) < .00001, $"Expected {expected}, got {actual}"); }
var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
var reset = now.AddDays(7).ToUnixTimeSeconds();
UsageWindow Window(double? used = 30, int? mins = 10080, double? end = null) => new(used, mins, end ?? reset);
Snapshot Snap(string account = "a", double used = 30) => new(account, now, [new("codex", "Codex", "primary", Window(used))]);
Check("daily allowance freezes while usage is deducted", () => { var a = DailyBudget.Reconcile(null, Window(), now, TimeZoneInfo.Utc)!; Near(a.Allowance, 10); var b = DailyBudget.Reconcile(a, Window(34), now.AddHours(1), TimeZoneInfo.Utc)!; Near(b.Allowance, 10); Near(b.LeftToday, 6); });
Check("overspending remains negative", () => { var b = DailyBudget.Reconcile(DailyBudget.Reconcile(null, Window(), now), Window(47), now)!; Near(b.LeftToday, -7); Near(b.OverBudget, 7); });
Check("next day does not deduct yesterday twice", () => { var a = DailyBudget.Reconcile(null, Window(), now, TimeZoneInfo.Utc)!; a = DailyBudget.Reconcile(a, Window(47), now, TimeZoneInfo.Utc)!; var b = DailyBudget.Reconcile(a, Window(47), now.AddDays(1), TimeZoneInfo.Utc)!; Near(b.Allowance, 53d / 6); Near(b.Consumed, 0); });
Check("fractional days preserved and final day capped", () => { Near(DailyBudget.Reconcile(null, Window(20, end: now.AddDays(2.5).ToUnixTimeSeconds()), now)!.Allowance, 32); Near(DailyBudget.Reconcile(null, Window(20, end: now.AddHours(4).ToUnixTimeSeconds()), now)!.Allowance, 80); });
Check("unknown fields and invalid timestamps never fabricate budget", () => { Assert(DailyBudget.Reconcile(null, Window(null), now) is null); Assert(DailyBudget.Reconcile(null, Window(mins: null), now) is null); Assert(DailyBudget.Reconcile(null, Window(end: double.MaxValue), now) is null); Assert(new UsageWindow(double.NaN, 300, reset).Remaining is null); });
Check("expired window waits for server confirmation", () => { Assert(DailyBudget.Reconcile(null, Window(end: now.ToUnixTimeSeconds()), now) is null); var p = new SessionPace(Window(20, 300, now.ToUnixTimeSeconds()), now); Assert(p.Expired && p.Allowance is null); });
Check("reset timestamp jitter preserves baseline", () => { var b = DailyBudget.Reconcile(null, Window(), now)!; var next = DailyBudget.Reconcile(b, Window(34, end: reset + 3), now)!; Near(next.Allowance, 10); Near(next.Consumed, 4); });
Check("usage decrease starts a fresh baseline", () => { var b = DailyBudget.Reconcile(null, Window(), now)!; var next = DailyBudget.Reconcile(b, Window(0), now)!; Near(next.StartingUsed, 0); Near(next.Allowance, 100d / 7); });
Check("timezone change restarts daily baseline", () => { var b = DailyBudget.Reconcile(null, Window(), now, TimeZoneInfo.Utc)!; var zone = TimeZoneInfo.CreateCustomTimeZone("Test zone", TimeSpan.FromHours(8), "Test zone", "Test zone"); var next = DailyBudget.Reconcile(b, Window(40), now, zone)!; Near(next.Consumed, 0); Assert(next.TimeZoneId == zone.Id); });
Check("remaining quota clamps correctly", () => { Near(Window(-10).Remaining, 100); Near(Window(200).Remaining, 0); });
Check("hourly allowance does not depend on midnight", () => { var p = new SessionPace(Window(40, 300, now.AddHours(3).ToUnixTimeSeconds()), now); Near(p.Allowance, 20); var q = new SessionPace(p.Window, now.AddHours(1)); Near(q.Allowance, 30); });
Check("final partial hour shows actual remaining quota", () => { var p = new SessionPace(Window(40, 300, now.AddMinutes(20).ToUnixTimeSeconds()), now); Assert(p.FinalHour); Near(p.Allowance, 60); });
Check("countdown rounds at hour boundary", () => { L.Language = "en"; Assert(new SessionPace(Window(0, 300, now.AddSeconds(7199).ToUnixTimeSeconds()), now).Countdown == "2h 0m"); });
Check("unknown reset countdown is explicit", () => { L.Language = "en"; var p = new SessionPace(new(20, 300, null), now); Assert(p.Allowance is null && p.Countdown.Contains("unknown")); });
ResetCredit Credit(double? expiry, string status = "available", string type = "codexRateLimits") => new("id", type, status, null, expiry, null, null);
Check("credit deadline uses current quota and earliest available expiry", () => { var s = Snap(used: 40) with { Credits = new(2, [Credit(now.AddHours(12).ToUnixTimeSeconds()), Credit(now.AddHours(10).ToUnixTimeSeconds())]) }; var p = CreditPace.Evaluate(s, s.Windows[0], false, now)!; Near(p.Allowance, 6); Assert(p.Expiry == now.AddHours(10)); Near(CreditPace.Evaluate(s, s.Windows[0], false, now.AddHours(5))!.Allowance, 12); });
Check("credit hourly pace begins at the 72-hour boundary", () => { var expiry = now.AddHours(72).AddSeconds(1); var s = Snap(used: 40) with { Credits = new(1, [Credit(expiry.ToUnixTimeSeconds())]) }; var w = s.Windows[0]; Assert(CreditPace.Evaluate(s, w, false, now) is null); Assert(!CreditPace.NeedsConfirmation(s, w, true, now)); var boundary = now.AddSeconds(1); var pace = CreditPace.Evaluate(s, w, false, boundary)!; Assert(pace.Expiry == expiry); Near(pace.Allowance, 60d / 72); Assert(CreditPace.NeedsConfirmation(s, w, true, boundary)); Assert(CreditPace.Evaluate(s, w, false, boundary.AddSeconds(1)) is not null); });
Check("credit deadline still must precede the selected natural reset", () => { var expiry = now.AddHours(72); var s = Snap() with { Credits = new(1, [Credit(expiry.ToUnixTimeSeconds())]) }; var w = s.Windows[0] with { Window = Window(end: expiry.ToUnixTimeSeconds()) }; Assert(CreditPace.Evaluate(s, w, false, now) is null); Assert(!CreditPace.NeedsConfirmation(s, w, true, now)); w = w with { Window = Window(end: expiry.AddSeconds(-1).ToUnixTimeSeconds()) }; Assert(CreditPace.Evaluate(s, w, false, now) is null); Assert(!CreditPace.NeedsConfirmation(s, w, true, now)); });
Check("eligible credit is selected among multiple credits", () => { var s = Snap(used: 40) with { Credits = new(3, [Credit(now.AddDays(4).ToUnixTimeSeconds()), Credit(now.AddHours(60).ToUnixTimeSeconds()), Credit(now.AddHours(10).ToUnixTimeSeconds(), "used"), Credit(double.MaxValue), Credit(null)]) }; var pace = CreditPace.Evaluate(s, s.Windows[0], false, now)!; Assert(pace.Expiry == now.AddHours(60)); Near(pace.Allowance, 1); Assert(CreditPace.NeedsConfirmation(s, s.Windows[0], true, now)); var farOnly = s with { Credits = new(2, [Credit(now.AddDays(4).ToUnixTimeSeconds()), Credit(double.MaxValue), Credit(null)]) }; Assert(CreditPace.Evaluate(farOnly, s.Windows[0], false, now) is null); Assert(!CreditPace.NeedsConfirmation(farOnly, s.Windows[0], true, now)); });
Check("credit final hour and expiry boundary", () => { var s = Snap(used: 40) with { Credits = new(1, [Credit(now.AddMinutes(30).ToUnixTimeSeconds())]) }; var p = CreditPace.Evaluate(s, s.Windows[0], false, now)!; Assert(p.FinalHour); Near(p.Allowance, 60); Assert(CreditPace.Evaluate(s, s.Windows[0], false, now.AddMinutes(30)) is null); });
Check("credit pace requires fresh complete account and eligible credit", () => { var s = Snap() with { Credits = new(1, [Credit(now.AddHours(10).ToUnixTimeSeconds())]) }; var w = s.Windows[0]; Assert(CreditPace.Evaluate(s, w, true, now) is null); Assert(CreditPace.Evaluate(s with { Credits = new(0, s.Credits!.Credits) }, w, false, now) is null); foreach (var c in new[] { Credit(null), Credit(double.MaxValue), Credit(now.ToUnixTimeSeconds()), Credit(now.AddDays(7).ToUnixTimeSeconds()), Credit(now.AddHours(10).ToUnixTimeSeconds(), "used"), Credit(now.AddHours(10).ToUnixTimeSeconds(), type: "other") }) Assert(CreditPace.Evaluate(s with { Credits = new(1, [c]) }, w, false, now) is null); Assert(CreditPace.Evaluate(s, w with { BucketId = "claude" }, false, now) is null); Assert(CreditPace.Evaluate(s, w with { Window = Window(null) }, false, now) is null); Assert(CreditPace.Evaluate(s, w with { Window = Window(end: now.ToUnixTimeSeconds()) }, false, now) is null); });
Check("expired and stale credits suspend pacing until confirmed", () => { var future = Credit(now.AddHours(10).ToUnixTimeSeconds()); var expired = Credit(now.AddSeconds(-1).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(2, [expired, future]) }; var w = s.Windows[0]; Assert(CreditPace.NeedsConfirmation(s, w, false, now)); Assert(CreditPace.Evaluate(s, w, false, now) is null); var cached = s with { Credits = new(1, [future]) }; Assert(CreditPace.NeedsConfirmation(cached, w, true, now)); Assert(!CreditPace.NeedsConfirmation(cached, w, false, now)); Assert(!CreditPace.NeedsConfirmation(cached with { Credits = new(0, [future]) }, w, true, now)); Assert(!CreditPace.NeedsConfirmation(s with { Credits = new(0, [expired]) }, w, false, now)); });
Check("old expired records do not suspend daily pacing or trigger expiry refresh", () => { var old = Credit(now.AddHours(-72).AddSeconds(-1).ToUnixTimeSeconds()); var far = Credit(now.AddHours(80).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(2, [old, far]) }; var w = s.Windows[0]; Assert(!CreditPace.HasExpiredCreditRequiringConfirmation(s, w, now)); Assert(!CreditPace.NeedsConfirmation(s, w, false, now)); Assert(!CreditPace.NeedsConfirmation(s, w, true, now)); Assert(CreditPace.Evaluate(s, w, false, now) is null); var boundary = s with { Credits = new(1, [Credit(now.AddHours(-72).ToUnixTimeSeconds())]) }; Assert(CreditPace.HasExpiredCreditRequiringConfirmation(boundary, w, now)); Assert(CreditPace.NeedsConfirmation(boundary, w, false, now)); });
Check("old expired record with an approaching credit requires refresh", () => { var old = Credit(now.AddHours(-73).ToUnixTimeSeconds()); var approaching = Credit(now.AddHours(70).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(2, [old, approaching]) }; var w = s.Windows[0]; Assert(CreditPace.HasExpiredCreditRequiringConfirmation(s, w, now)); Assert(CreditPace.NeedsConfirmation(s, w, false, now)); Assert(CreditPace.Evaluate(s, w, false, now) is null); var sooner = w with { Window = Window(end: now.AddHours(60).ToUnixTimeSeconds()) }; Assert(!CreditPace.HasExpiredCreditRequiringConfirmation(s, sooner, now)); Assert(!CreditPace.NeedsConfirmation(s, sooner, true, now)); Assert(!CreditPace.HasExpiredCreditRequiringConfirmation(s, w with { Window = Window(null) }, now)); });
Check("per-account history isolation", () => { var h = new ProviderHistory(); h.Ingest(Snap()); h.Ingest(Snap(used: 40)); Near(h.Budgets.Values.Single().Consumed, 10); h.Ingest(Snap("b", 40)); Near(h.Budgets.Values.Single().Consumed, 0); });
Check("provider selection is independent", () => { var h = new History(); h.For("codex").Ingest(Snap()); h.For("claude").Ingest(Snap("claude")); h.For("claude").SelectedId = "fable"; Assert(h.For("codex").SelectedId == "codex/primary"); });
Check("budget serialization preserves negative balance", () => { var h = new ProviderHistory(); h.Ingest(Snap()); h.Ingest(Snap(used: 47)); var copy = JsonSerializer.Deserialize<ProviderHistory>(JsonSerializer.Serialize(h, Json.Options), Json.Options)!; Near(copy.Budgets.Values.Single().LeftToday, -7); });
Check("Codex named buckets take precedence over legacy", () => { using var d = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":90}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":30,"windowDurationMins":10080}}}}"""); var w = UsageParser.Codex(d.RootElement); Assert(w.Count == 2 && w[0].Window.WindowDurationMins == 10080); Near(w[0].Window.Remaining, 70); });
Check("Codex null usage stays unknown", () => { using var d = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":null}}}"""); Assert(UsageParser.Codex(d.RootElement)[0].Window.Remaining is null); });
string ClaudeReply(int status = 200) => JsonSerializer.Serialize(new { status, organization = "00000000-0000-0000-0000-000000000001", organizations = new[] { new { uuid = "00000000-0000-0000-0000-000000000001", name = "Test" } }, usage = new { seven_day = new { utilization = 30 }, five_hour = new { utilization = 40, resets_at = "2026-10-21T15:00:00Z" }, seven_day_fable = new { utilization = 0, resets_at = "2026-10-28T12:00:00Z" } } });
Check("Claude Fable zero is valid and independently scoped", () => { using var d = JsonDocument.Parse(ClaudeReply()); var s = UsageParser.Claude(d.RootElement, now); Assert(s.Windows.Count == 3); Near(s.Windows.Single(w => w.BucketId == "claude_fable").Window.Remaining, 100); Assert(s.Windows[0].Window.ResetDate is null); });
Check("Claude requires organization membership", () => { using var d = JsonDocument.Parse(ClaudeReply().Replace("\"organizations\":[{\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"Test\"}]", "\"organizations\":[]")); try { UsageParser.Claude(d.RootElement, now); throw new Exception("Accepted missing membership"); } catch (PaceException) { } });
Check("Claude auth and rate-limit errors are typed", () => { foreach (var status in new[] { 401, 403, 409, 429 }) { using var d = JsonDocument.Parse(ClaudeReply(status)); try { UsageParser.Claude(d.RootElement, now); throw new Exception("Accepted HTTP error"); } catch (PaceException e) { Assert(status == 429 ? e.RetrySeconds >= 300 : e.NeedsLogin); } } });
Check("Claude origin is exact and HTTPS-only", () => { Assert(ClaudeBridge.IsClaude("https://claude.ai/settings/usage")); foreach (var s in new[] { "http://claude.ai/", "https://claude.ai.evil.test/", "https://claude.ai:444/", "https://evil@claude.ai/" }) Assert(!ClaudeBridge.IsClaude(s)); });
Check("Claude bridge filters payload and uses GET only", () => { Assert(ClaudeBridge.Script.Contains("method:'GET'") && !ClaudeBridge.Script.Contains("document.cookie") && ClaudeBridge.Script.Contains("fable.length === 1") && ClaudeBridge.Script.Contains("display_name === 'Fable'")); });
Check("missing activity is distinct from an empty history", () => { Assert(new AccountActivity(null).Recent(now).DailyUsageBuckets is null); Assert(new AccountActivity([]).Recent(now).DailyUsageBuckets!.Count == 0); });
Check("activity excludes today, invalid dates and duplicates", () => { var a = new AccountActivity([new("2026-10-21", 999), new("2026-10-20", 100), new("2026-10-20", 150), new("2026-02-30", 2), new("2026-10-19", double.NaN)]).Recent(now); Assert(a.DailyUsageBuckets!.Count == 1); Near(a.DailyUsageBuckets[0].Tokens, 150); });
Check("sparse history does not invent patterns", () => { var p = new UsagePattern(new([new("2026-10-20", 100)]), now); Assert(!p.Sufficient && p.BusyWeekday is null && p.RecentRatio is null); });
var days = Enumerable.Range(1, 30).Select(i => new DailyActivity(now.AddDays(-i).ToString("yyyy-MM-dd"), i <= 7 ? 200 : 100)).ToList();
Check("recent trend uses observed valid days", () => { var p = new UsagePattern(new(days), now); Assert(p.Sufficient && p.HighDemandSoon); Near(p.RecentRatio, 2); });
Check("reset advice waits for a nearby natural reset", () => { var s = Snap() with { Credits = new(1, []), Windows = [new("codex", "Codex", "primary", Window(99, 300, now.AddHours(2).ToUnixTimeSeconds()))] }; Assert(ResetAdvice.Evaluate(s, new(null, now), false, false, now).Kind == "wait"); });
Check("depleted long window can justify reset without history", () => { var s = Snap(used: 100) with { Credits = new(1, []) }; Assert(ResetAdvice.Evaluate(s, new(null, now), false, false, now).Kind == "consider"); });
Check("stale usage blocks reset advice", () => Assert(ResetAdvice.Evaluate(Snap(), new(new(days), now), true, true, now).Kind == "refresh"));
Check("expired credit forces refresh", () => { var s = Snap() with { Credits = new(1, [new("id", "codexRateLimits", "available", null, now.AddSeconds(-1).ToUnixTimeSeconds(), null, null)]) }; Assert(ResetAdvice.Evaluate(s, new(new(days), now), true, false, now).Kind == "refresh"); });
Check("public evidence rejects unsafe and unrelated links", () => { Assert(ResetEvent.Evidence("https://x.com/thsottiaux/status/123") is not null); foreach (var u in new[] { "javascript:alert(1)", "https://x.com/another/status/123", "https://x.com.evil.test/thsottiaux/status/123", "https://x.com:444/thsottiaux/status/123", "https://x.com/thsottiaux/status/abc" }) Assert(ResetEvent.Evidence(u) is null); });
RadarSnapshot Radar(string status = "recorded_today", string source = "online", int age = 0) => new(new(status, now.AddMinutes(-age).ToString("O"), now.ToString("O"), null, null, [new("a", "global_reset", "recorded", now.ToString("O"), null, "minute", null, "https://x.com/thsottiaux/status/123", null)], [new("codex-resets", source, now.ToString("O"))]), now);
Check("public feed freshness needs all source checks", () => { Assert(Radar().Fresh(now)); Assert(!Radar(age: 31).Fresh(now)); Assert(!Radar(source: "offline").Fresh(now)); Assert(!Radar(status: "unknown").Fresh(now)); });
Check("public reset date is evaluated in local timezone", () => { L.Language = "en"; Assert(Radar().Assessment(now).Title.Contains("recorded today")); Assert(Radar().Assessment(now, true).Title.Contains("unconfirmed")); });
Check("date-only public events do not claim precise delivery", () => { var r = Radar(); var e = r.Feed.Events![0] with { AnnouncedAt = "2026-10-21", TimePrecision = "day" }; r = r with { Feed = r.Feed with { Events = [e] } }; L.Language = "en"; Assert(r.Assessment(now).Title.StartsWith("No global")); });
Check("language switch also updates existing error objects", () => { var e = PaceException.Login(); L.Language = "en"; Assert(e.Message.Contains("Sign in")); L.Language = "zh-Hans"; Assert(e.Message.Contains("订阅")); });
Check("atomic storage and preferences round-trip", () => { var root = Path.Combine(Path.GetTempPath(), "AgentPaceTests-" + Guid.NewGuid()); try { var s = new Storage(root); s.Write("settings.json", new Settings { Language = "zh-Hans", ClaudeEnabled = true }); Assert(s.Read<Settings>("settings.json").Language == "zh-Hans"); Assert(!File.Exists(Path.Combine(root, "settings.json.tmp"))); } finally { Directory.Delete(root, true); } });
Check("missing reset count is not silently zero", () => { try { JsonSerializer.Deserialize<ResetCredits>("{}", Json.Options); throw new Exception("Missing count accepted"); } catch (JsonException) { } });
Check("malformed date strings cannot enter public-event UI", () => { foreach (var date in new[] { "1/1/26", "yesterday", "2026-10-21T12:00:00", "2026-02-30" }) Assert(Json.Date(date) is null); Assert(Json.Date("2026-10-21") is not null); Assert(Json.Date("2026-10-21T12:00:00+00:00") is not null); });
ProcessStartInfo FakeStart(string mode)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        start.ArgumentList.Add(typeof(UsageWindow).Assembly.Location.Replace("AgentPace.Core.dll", "AgentPace.Checks.dll"));
    start.ArgumentList.Add("--fake-codex");
    start.ArgumentList.Add(mode);
    return start;
}
await AsyncCheck("Codex JSON-RPC integration and optional history", async () => { var s = await CodexClient.Fetch(FakeStart("normal"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1 && s.Activity?.DailyUsageBuckets?.Count == 1); Assert(!s.AccountKey.Contains("test@example.com")); });
await AsyncCheck("old Codex server without history still returns quota", async () => { var s = await CodexClient.Fetch(FakeStart("old"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1 && s.Activity is null); });
await AsyncCheck("unsolicited server requests are rejected", async () => { var s = await CodexClient.Fetch(FakeStart("approval"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1); });
await AsyncCheck("Codex hung process has a bounded timeout", async () => { var time = Stopwatch.StartNew(); try { await CodexClient.Fetch(FakeStart("hang"), TimeSpan.FromMilliseconds(500)); throw new Exception("Accepted hung process"); } catch (PaceException) { } Assert(time.Elapsed < TimeSpan.FromSeconds(5)); });
Console.WriteLine($"{passed}/{passed + failed} checks passed");
Environment.ExitCode = failed > 0 ? 1 : 0;
async Task Fake()
{
    var mode = args.Last();
    if (mode == "hang")
    {
        await Task.Delay(10000);
        return;
    }
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        using var d = JsonDocument.Parse(line);
        var m = d.RootElement;
        var method = m.Str("method");
        var id = m.Num("id");
        if (method == "initialize")
        {
            Console.WriteLine("{\"id\":1,\"result\":{}}");
            if (mode == "approval")
                Console.WriteLine("{\"id\":99,\"method\":\"item/commandExecution/requestApproval\"}");
        }
        else if (id == 99)
        {
            if (m.Get("error").GetValueOrDefault().Num("code") != -32601)
                Environment.Exit(4);
        }
        else if (method == "account/read")
            Console.WriteLine("{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"test@example.com\"}}}");
        else if (method == "account/rateLimits/read")
            Console.WriteLine("{\"id\":3,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":30,\"windowDurationMins\":10080,\"resetsAt\":1999999999}}}}");
        else if (method == "account/usage/read")
        {
            Console.WriteLine(mode == "old" ? "{\"id\":4,\"error\":{\"code\":-32601}}" : "{\"id\":4,\"result\":{\"dailyUsageBuckets\":[{\"startDate\":\"" + DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") + "\",\"tokens\":100}]}}");
            return;
        }
        else if (method != "initialized")
            Environment.Exit(5);
    }
}
