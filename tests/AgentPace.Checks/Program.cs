using System.Diagnostics;
using System.Text.Json;
using AgentPace.Core;
if (args.Contains("--fake-codex")) { await Fake(); return; }
if (args.Contains("--fake-lock")) { try { var storage = new Storage(Environment.GetEnvironmentVariable("FAKE_LOCK_ROOT")!); using var held = Environment.GetEnvironmentVariable("FAKE_AUTH_LOCK") == "1" ? storage.AcquireAutoResetAuthorizationLock() : storage.AcquireAutoResetLock(); } catch (IOException) { Environment.ExitCode = 7; } return; }
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
Check("automatic reset selects only fresh, identified credits within five minutes", () => { var expiry = now.AddMinutes(5); var s = Snap() with { Credits = new(2, [Credit(expiry.ToUnixTimeSeconds()), new("later", "codexRateLimits", "available", null, now.AddMinutes(6).ToUnixTimeSeconds(), null, null)]) }; Assert(AutoReset.Due(s, now)?.Id == "id"); Assert(AutoReset.Due(s, now.AddSeconds(-1)) is null); Assert(AutoReset.Due(s, expiry) is null); Assert(AutoReset.Due(s with { FetchedAt = now.AddMinutes(-2) }, now) is null); Assert(AutoReset.Due(s with { Credits = new(1, [Credit(null)]) }, now) is null); });
Check("automatic reset journal reuses key after restart and blocks simultaneous credits", () => { var expiry = now.AddMinutes(5); var s = Snap() with { Credits = new(2, [Credit(expiry.ToUnixTimeSeconds()), new("other", "codexRateLimits", "available", null, expiry.ToUnixTimeSeconds(), null, null)]) }; var journal = new AutoResetJournal(); var a = AutoReset.Prepare(journal, s, now)!; var restored = JsonSerializer.Deserialize<AutoResetJournal>(JsonSerializer.Serialize(journal, Json.Options), Json.Options)!; Assert(AutoReset.Prepare(restored, s, now.AddSeconds(1))!.IdempotencyKey == a.IdempotencyKey); restored.Attempts[0] = a with { Outcome = "reset" }; var next = s with { Credits = new(1, [s.Credits!.Credits![1]]) }; Assert(AutoReset.Prepare(restored, next, now.AddSeconds(1)) is null); restored.Attempts[0] = a; var later = next with { FetchedAt = now.AddMinutes(6), Credits = new(1, [Credit(now.AddMinutes(10).ToUnixTimeSeconds()) with { Id = "new" }]) }; Assert(AutoReset.Prepare(restored, later, now.AddMinutes(6)) is null); Assert(!AutoReset.IsEligible(s, "other-account", a.CreditId, a.Expiry, now)); Assert(!AutoReset.IsEligible(s, s.AccountKey, a.CreditId, a.Expiry!.Value.AddSeconds(1), now)); });
Check("clock rollback cannot trigger another automatic reset", () => { var s = Snap() with { Credits = new(1, [Credit(now.AddMinutes(4).ToUnixTimeSeconds())]) }; var journal = new AutoResetJournal { Attempts = [new("a", "previous", now.AddMinutes(10), Guid.NewGuid().ToString(), now.AddMinutes(1), "reset")] }; Assert(AutoReset.Prepare(journal, s, now) is null); });
Check("automatic reset defaults off", () => Assert(!new Settings().AutoResetEnabled));
Check("manual reset can select any identified future or undated credit without automatic authorization", () => {
    var near = Credit(now.AddMinutes(4).ToUnixTimeSeconds()); var far = Credit(now.AddDays(30).ToUnixTimeSeconds()) with { Id = "far" }; var undated = Credit(null) with { Id = "undated" };
    var s = Snap() with { Credits = new(3, [near, far, undated]) };
    Assert(!new Settings().AutoResetEnabled && AutoReset.Due(s, now)?.Id == near.Id);
    Assert(ManualReset.Eligible(s, "a", far.Id, far.Expiry, now) == far);
    Assert(ManualReset.Eligible(s, "a", undated.Id, null, now) == undated);
    Assert(ManualReset.Prepare(new(), s, "a", far.Id, far.Expiry, now)?.Manual == true);
});
Check("manual reset rejects stale account, ambiguous IDs, invalid expiry and changed selection", () => {
    var c = Credit(now.AddDays(8).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(1, [c]) };
    Assert(ManualReset.Eligible(s, "other", c.Id, c.Expiry, now) is null);
    Assert(ManualReset.Eligible(s, "a", "other-id", c.Expiry, now) is null);
    Assert(ManualReset.Eligible(s, "a", c.Id, c.Expiry!.Value.AddSeconds(1), now) is null);
    Assert(ManualReset.Eligible(s, "a", c.Id, null, now) is null);
    Assert(ManualReset.Eligible(s with { FetchedAt = now.AddSeconds(1) }, "a", c.Id, c.Expiry, now) is null);
    Assert(ManualReset.Eligible(s with { FetchedAt = now.AddSeconds(-61) }, "a", c.Id, c.Expiry, now) is null);
    Assert(ManualReset.Eligible(s with { Credits = new(2, [c, c]) }, "a", c.Id, c.Expiry, now) is null);
    foreach (var invalid in new[] { Credit(now.ToUnixTimeSeconds()), Credit(double.MaxValue), Credit(null) with { Id = "" }, c with { Status = "used" }, c with { ResetType = "other" } })
        Assert(ManualReset.Eligible(s with { Credits = new(1, [invalid]) }, "a", invalid.Id, invalid.Expiry, now) is null);
});
Check("manual unknown retries preserve key after restart and block other credits", () => {
    var c = Credit(now.AddDays(8).ToUnixTimeSeconds()); var other = c with { Id = "other" }; var s = Snap() with { Credits = new(2, [c, other]) };
    var j = new AutoResetJournal(); var first = ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now)!;
    j = JsonSerializer.Deserialize<AutoResetJournal>(JsonSerializer.Serialize(j, Json.Options), Json.Options)!;
    Assert(ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now.AddSeconds(1))!.IdempotencyKey == first.IdempotencyKey);
    Assert(ManualReset.Prepare(j, s, "a", other.Id, other.Expiry, now.AddSeconds(1)) is null);
    Assert(ManualReset.Prepare(j, s with { AccountKey = "b" }, "b", other.Id, other.Expiry, now.AddSeconds(1)) is not null);
});
Check("manual retries of automatic unknowns keep the original key; automatic cannot resume manual unknowns", () => {
    var c = Credit(now.AddMinutes(4).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(1, [c]) }; var j = new AutoResetJournal();
    var automatic = AutoReset.Prepare(j, s, now)!;
    Assert(ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now.AddSeconds(1))!.IdempotencyKey == automatic.IdempotencyKey);
    j = new(); Assert(ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now) is not null); Assert(AutoReset.Prepare(j, s, now) is null);
});
Check("confirmed no-op permits a newly confirmed manual request; terminal outcomes never replay", () => {
    foreach (var outcome in new[] { "nothingToReset", "noCredit", "reset", "alreadyRedeemed", "manuallyReviewed" }) {
        var c = Credit(now.AddDays(8).ToUnixTimeSeconds()); var s = Snap() with { Credits = new(1, [c]) }; var j = new AutoResetJournal();
        var a = ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now)!; j.Attempts[0] = a with { Outcome = outcome };
        Assert(ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now) is null);
        var next = ManualReset.Prepare(j, s with { FetchedAt = now.AddSeconds(1) }, "a", c.Id, c.Expiry, now.AddSeconds(1));
        if (outcome is "nothingToReset" or "noCredit") { Assert(next is not null && next.IdempotencyKey != a.IdempotencyKey); j.Attempts[^1] = next! with { Outcome = "reset" }; Assert(ManualReset.Prepare(j, s with { FetchedAt = now.AddSeconds(2) }, "a", c.Id, c.Expiry, now.AddSeconds(2)) is null); }
        else Assert(next is null);
    }
});
Check("manual and automatic share cooldown across credits and resist clock rollback", () => {
    var c = Credit(now.AddDays(8).ToUnixTimeSeconds()); var other = c with { Id = "other" }; var s = Snap() with { Credits = new(2, [c, other]) }; var j = new AutoResetJournal();
    var a = ManualReset.Prepare(j, s, "a", c.Id, c.Expiry, now)!; j.Attempts[0] = a with { Outcome = "reset" };
    Assert(ManualReset.Prepare(j, s with { FetchedAt = now.AddMinutes(-1) }, "a", other.Id, other.Expiry, now.AddMinutes(-1)) is null);
    Assert(ManualReset.Prepare(j, s with { FetchedAt = now.AddMinutes(4) }, "a", other.Id, other.Expiry, now.AddMinutes(4)) is null);
    Assert(ManualReset.Prepare(j, s with { FetchedAt = now.AddMinutes(5) }, "a", other.Id, other.Expiry, now.AddMinutes(5)) is not null);
});
Check("per-account history isolation", () => { var h = new ProviderHistory(); h.Ingest(Snap()); h.Ingest(Snap(used: 40)); Near(h.Budgets.Values.Single().Consumed, 10); h.Ingest(Snap("b", 40)); Near(h.Budgets.Values.Single().Consumed, 0); });
Check("reset plan includes expiry date and does not add natural resets", () => {
    var date = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    var w = new MeteredWindow("codex", "Codex", "primary", new(0, 10080, date.AddDays(7).ToUnixTimeSeconds()));
    var s = new Snapshot("a", date, [w], new(1, [Credit(date.AddDays(5).ToUnixTimeSeconds())]));
    var b = ResetPlanBudget.Reconcile(null, s, w, date, TimeZoneInfo.Utc)!;
    Assert(b.PlanningDays == 6); Near(b.StartingTotal, 200); Near(b.Allowance, 200d / 6); Near(b.LeftToday(s, w, date), 200d / 6);
    var firstReset = w with { Window = w.Window with { ResetsAt = date.AddDays(4).ToUnixTimeSeconds() } };
    var beforeExpiry = ResetPlanBudget.Reconcile(null, s, firstReset, date, TimeZoneInfo.Utc)!;
    Assert(beforeExpiry.PlanningDays == 6); Near(beforeExpiry.StartingTotal, 200); Near(beforeExpiry.Allowance, 200d / 6); Near(beforeExpiry.Capacity(s, firstReset, date), 200);
    Assert(CreditPace.Evaluate(s, firstReset, false, date) is null);
    Assert(CreditPace.Evaluate(s, w, false, date) is null);
});
Check("reset plan only counts unique verified earliest batch", () => {
    var c = Credit(now.AddDays(5).ToUnixTimeSeconds()); var w = Snap().Windows[0];
    var s = Snap() with { Credits = new(3, [c, c with { Id = "same-day" }, c with { Id = "later", ExpiresAt = now.AddDays(6).ToUnixTimeSeconds() }]) };
    var b = ResetPlanBudget.Reconcile(null, s, w, now, TimeZoneInfo.Utc)!; Assert(b.CreditIds.Count == 2); Near(b.StartingTotal, 270);
    Assert(ResetPlanBudget.Reconcile(null, s with { Credits = new(1, s.Credits!.Credits) }, w, now) is null);
    Assert(ResetPlanBudget.Reconcile(null, s with { Credits = new(2, [c, c]) }, w, now) is null);
    Assert(ResetPlanBudget.Reconcile(null, s with { Credits = new(1, [Credit(null)]) }, w, now) is null);
});
Check("reset plan freezes daily allowance across redemption and tracks observed use once", () => {
    var s = Snap(used: 0) with { Credits = new(1, [Credit(now.AddDays(5).ToUnixTimeSeconds())]) }; var w = s.Windows[0];
    var b = ResetPlanBudget.Reconcile(null, s, w, now, TimeZoneInfo.Utc)!;
    var spent = w with { Window = w.Window with { UsedPercent = 40 } }; b = ResetPlanBudget.Reconcile(b, s, spent, now.AddHours(1), TimeZoneInfo.Utc)!; Near(b.Consumed, 40);
    var restored = w with { Window = w.Window with { UsedPercent = 0, ResetsAt = reset + 3600 } }; var after = s with { Credits = new(0, []), Windows = [restored] };
    b = ResetPlanBudget.Reconcile(b, after, restored, now.AddHours(2), TimeZoneInfo.Utc)!; Near(b.Consumed, 40); Near(b.Allowance, 200d / 6); Near(b.Capacity(after, restored, now), 100);
    b = ResetPlanBudget.Reconcile(b, after, restored, now.AddHours(2), TimeZoneInfo.Utc)!; Near(b.Consumed, 40); Near(b.LeftToday(after, restored, now), 200d / 6 - 40);
    Assert(ResetPlanBudget.Reconcile(b, after, restored, now.AddDays(1), TimeZoneInfo.Utc) is null);
});
Check("reset plan keeps ledger but cannot display unknown credit capacity", () => {
    var s = Snap() with { Credits = new(1, [Credit(now.AddDays(5).ToUnixTimeSeconds())]) }; var w = s.Windows[0];
    var b = ResetPlanBudget.Reconcile(null, s, w, now, TimeZoneInfo.Utc)!; var missing = s with { Credits = null };
    Assert(ResetPlanBudget.Reconcile(b, missing, w, now.AddDays(1), TimeZoneInfo.Utc) == b); Assert(b.LeftToday(missing, w, now) is null);
    var h = new ProviderHistory(); h.Ingest(s); Assert(h.ResetPlans.Count == 1); h.Ingest(Snap("b")); Assert(h.ResetPlans.Count == 0);
});
Check("reset plan counts confirmed new-cycle use and never resurrects a spent credit", () => {
    var s = Snap(used: 0) with { Credits = new(1, [Credit(now.AddDays(5).ToUnixTimeSeconds())]) }; var w = s.Windows[0];
    var b = ResetPlanBudget.Reconcile(null, s, w, now, TimeZoneInfo.Utc)!; var spent = w with { Window = w.Window with { UsedPercent = 80 } };
    b = ResetPlanBudget.Reconcile(b, s, spent, now.AddHours(1), TimeZoneInfo.Utc)!;
    var resetWindow = w with { Window = w.Window with { UsedPercent = 20, ResetsAt = reset + 7200 } }; var redeemed = s with { Credits = new(0, []) };
    b = ResetPlanBudget.Reconcile(b, redeemed, resetWindow, now.AddHours(2), TimeZoneInfo.Utc)!; Near(b.Consumed, 100); Near(b.Capacity(redeemed, resetWindow, now), 80);
    b = ResetPlanBudget.Reconcile(b, s, resetWindow, now.AddHours(3), TimeZoneInfo.Utc)!; Near(b.Capacity(s, resetWindow, now), 80);
    var earlier = resetWindow with { Window = resetWindow.Window with { ResetsAt = now.AddDays(2).ToUnixTimeSeconds() } };
    Near(ResetPlanBudget.Reconcile(null, s, w, now, TimeZoneInfo.Utc)!.Capacity(s, earlier, now), 180);
});
Check("observed natural reset preserves same-day spending without forecasting another refill", () => {
    var date = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    var w = new MeteredWindow("codex", "Codex", "primary", new(0, 10080, date.AddHours(1).ToUnixTimeSeconds()));
    var s = new Snapshot("a", date, [w], new(1, [Credit(date.AddDays(1).ToUnixTimeSeconds())]));
    var b = ResetPlanBudget.Reconcile(null, s, w, date, TimeZoneInfo.Utc)!; Near(b.StartingTotal, 200); Near(b.Allowance, 100);
    var spent = w with { Window = w.Window with { UsedPercent = 40 } };
    b = ResetPlanBudget.Reconcile(b, s, spent, date.AddMinutes(30), TimeZoneInfo.Utc)!; Near(b.Consumed, 40); Near(b.Capacity(s, spent, date.AddMinutes(30)), 160);
    var resetWindow = w with { Window = w.Window with { UsedPercent = 10, ResetsAt = date.AddDays(7).ToUnixTimeSeconds() } };
    var refreshed = s with { FetchedAt = date.AddHours(2), Windows = [resetWindow] };
    b = ResetPlanBudget.Reconcile(b, refreshed, resetWindow, refreshed.FetchedAt, TimeZoneInfo.Utc)!;
    Near(b.Consumed, 50); Near(b.Allowance, 100); Near(b.StartingTotal, 200); Near(b.Capacity(refreshed, resetWindow, refreshed.FetchedAt), 190); Near(b.LeftToday(refreshed, resetWindow, refreshed.FetchedAt), 50);
    Assert(b.RemainingCreditIds!.Count == 1);
});
Check("reset plan local date includes DST expiry day", () => {
    var date = new DateTimeOffset(2026, 10, 31, 19, 0, 0, TimeSpan.Zero); var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    var w = new MeteredWindow("codex", "Codex", "primary", new(0, 10080, date.AddDays(7).ToUnixTimeSeconds())); var s = new Snapshot("a", date, [w], new(1, [Credit(date.AddDays(2).ToUnixTimeSeconds())]));
    Assert(ResetPlanBudget.Reconcile(null, s, w, date, zone)!.PlanningDays == 3);
});
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
Check("token activity preserves today and summary independently of history patterns", () => {
    var a = new AccountActivity([new("2026-10-21", 999), new("2026-10-20", 150), new("2026-09-21", 200)], new(2000, 999)).Clean(now);
    Near(a.TodayTokens(now), 999); Near(a.Last30Tokens(now), 1149); Assert(a.Recent(now).DailyUsageBuckets!.Count == 2); Near(a.Summary!.LifetimeTokens, 2000);
    var h = new ProviderHistory(); h.Ingest(Snap() with { Activity = a }); Near(h.Activity!.TodayTokens(now), 999);
    h.Ingest(Snap("other")); Assert(h.Activity is null && h.ActivityFetchedAt is null);
});
Check("summary-only and missing tokens stay explicit", () => {
    var a = new AccountActivity(null, new(1200, null)).Clean(now); Near(a.Summary!.LifetimeTokens, 1200); Assert(a.TodayTokens(now) is null && a.Last30Tokens(now) is null);
    var h = new ProviderHistory(); h.Ingest(Snap() with { Activity = a }); Near(h.Activity!.Summary!.LifetimeTokens, 1200);
    Assert(new AccountActivity([], new(-1, 9_007_199_254_740_992)).Clean(now).Summary is { LifetimeTokens: null, PeakDailyTokens: null });
    Assert(new AccountActivity([new("2026-10-21", 0)]).TodayTokens(now) == 0);
});
Check("Claude transcript tokens deduplicate partial blocks and copies, include caches and exclude Cowork", () => {
    var root = Path.Combine(Path.GetTempPath(), "AgentPaceTokens-" + Guid.NewGuid()); Directory.CreateDirectory(root);
    string Row(string id, int input, int output, string entrypoint = "claude-desktop", string date = "2026-10-21T10:00:00Z") => JsonSerializer.Serialize(new { type = "assistant", timestamp = date, entrypoint, sessionId = "session", requestId = "req-" + id, message = new { id, model = "claude-test", usage = new { input_tokens = input, output_tokens = output, cache_creation_input_tokens = 30, cache_read_input_tokens = 40 } } });
    try {
        File.WriteAllLines(Path.Combine(root, "a.jsonl"), [Row("a", 10, 2), Row("a", 10, 5), Row("cowork", 900, 900, "local-agent"), Row("old", 9, 9, date: "2026-09-01T10:00:00Z")]);
        File.WriteAllLines(Path.Combine(root, "b.jsonl"), [Row("a", 10, 5), Row("b", 20, 6, "cli", "2026-10-20T10:00:00Z"), "{\"type\":"]);
        var result = ClaudeTokens.Read(root, now, TimeZoneInfo.Utc); Assert(result.Messages == 2 && result.RecordedDays == 2 && result.Incomplete); Near(result.Today?.Total, 85); Near(result.Last30Days?.Total, 181);
        File.WriteAllText(Path.Combine(root, "a.jsonl"), Row("yesterday", 0, 0, date: "2026-10-20T10:00:00Z")); File.Delete(Path.Combine(root, "b.jsonl"));
        Assert(ClaudeTokens.Read(root, now, TimeZoneInfo.Utc).Today is null);
    } finally { Directory.Delete(root, true); }
});
Check("Claude absent or incomplete usage is unknown, never inferred from quota", () => {
    var root = Path.Combine(Path.GetTempPath(), "AgentPaceTokens-" + Guid.NewGuid()); Assert(ClaudeTokens.Read(root, now).Last30Days is null); Directory.CreateDirectory(root);
    try { File.WriteAllText(Path.Combine(root, "partial.jsonl"), """{"type":"assistant","timestamp":"2026-10-21T10:00:00Z","sessionId":"s","message":{"id":"m","usage":{"input_tokens":2,"output_tokens":3}}}"""); var result = ClaudeTokens.Read(root, now, TimeZoneInfo.Utc); Assert(result.Today is null && result.Last30Days is null && result.Incomplete); }
    finally { Directory.Delete(root, true); }
});
Check("Claude explicit zero and fallback session identity remain exact", () => {
    var root = Path.Combine(Path.GetTempPath(), "AgentPaceTokens-" + Guid.NewGuid()); Directory.CreateDirectory(root);
    try {
        var row = """{"type":"assistant","timestamp":"2026-10-21T10:00:00Z","sessionId":"s","message":{"id":"m","model":"real-model","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}""";
        File.WriteAllLines(Path.Combine(root, "zero.jsonl"), [row, row, row.Replace("\"sessionId\":\"s\"", "\"sessionId\":\"other\""), row.Replace("real-model", "<synthetic>")]);
        var result = ClaudeTokens.Read(root, now, TimeZoneInfo.Utc); Near(result.Today?.Total, 0); Assert(result.Messages == 2 && !result.Incomplete);
    } finally { Directory.Delete(root, true); }
});
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
await AsyncCheck("Codex JSON-RPC integration preserves today and optional token summary", async () => { var s = await CodexClient.Fetch(FakeStart("normal"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1 && s.Activity?.DailyUsageBuckets?.Count == 2); Near(s.Activity!.TodayTokens(s.FetchedAt), 50); Near(s.Activity.Summary!.LifetimeTokens, 150); Assert(s.Activity.Recent(s.FetchedAt).DailyUsageBuckets!.Count == 1); Assert(!s.AccountKey.Contains("test@example.com")); });
await AsyncCheck("old Codex server without history still returns quota", async () => { var s = await CodexClient.Fetch(FakeStart("old"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1 && s.Activity is null); });
await AsyncCheck("unsolicited server requests are rejected", async () => { var s = await CodexClient.Fetch(FakeStart("approval"), TimeSpan.FromSeconds(10)); Assert(s.Windows.Count == 1); });
await AsyncCheck("Codex hung process has a bounded timeout", async () => { var time = Stopwatch.StartNew(); try { await CodexClient.Fetch(FakeStart("hang"), TimeSpan.FromMilliseconds(500)); throw new Exception("Accepted hung process"); } catch (PaceException) { } Assert(time.Elapsed < TimeSpan.FromSeconds(5)); });
await AsyncCheck("automatic redemption sends exact ID and idempotency key to fake server", async () => { var expiry = DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds(); var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = expiry.ToString(); var account = UsageParser.Hash(JsonSerializer.Serialize(new { identity = "test@example.com", workspace = (JsonElement?)null })); var req = new ConsumeRequest(account, "fake-credit", DateTimeOffset.FromUnixTimeSeconds(expiry), "test-key"); var result = await CodexClient.Consume(start, TimeSpan.FromSeconds(10), req); Assert(result.Outcome == "reset"); });
await AsyncCheck("automatic redemption rejects changed account without sending", async () => { var expiry = DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds(); var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = expiry.ToString(); try { await CodexClient.Consume(start, TimeSpan.FromSeconds(10), new("different-account", "fake-credit", DateTimeOffset.FromUnixTimeSeconds(expiry), "test-key")); throw new Exception("Accepted wrong account"); } catch (PaceException) { } });
await AsyncCheck("automatic reset lock excludes another process", async () => { var root = Path.Combine(Path.GetTempPath(), "AgentPaceLockTest-" + Guid.NewGuid()); try { foreach (var auth in new[] { false, true }) using (var lease = auth ? new Storage(root).AcquireAutoResetAuthorizationLock() : new Storage(root).AcquireAutoResetLock()) { var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(UsageWindow).Assembly.Location.Replace("AgentPace.Core.dll", "AgentPace.Checks.dll")); start.ArgumentList.Add("--fake-lock"); start.Environment["FAKE_LOCK_ROOT"] = root; if (auth) start.Environment["FAKE_AUTH_LOCK"] = "1"; using var child = Process.Start(start)!; await child.WaitForExitAsync(); Assert(child.ExitCode == 7, "Concurrent process acquired the operation lock"); } } finally { if (Directory.Exists(root)) Directory.Delete(root, true); } });
await AsyncCheck("automatic redemption honors revocation before sending", async () => { var expiry = DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds(); var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = expiry.ToString(); var account = UsageParser.Hash(JsonSerializer.Serialize(new { identity = "test@example.com", workspace = (JsonElement?)null })); try { await CodexClient.Consume(start, TimeSpan.FromSeconds(10), new(account, "fake-credit", DateTimeOffset.FromUnixTimeSeconds(expiry), "test-key", () => false)); throw new Exception("Accepted disabled authorization"); } catch (PaceException) { } });
await AsyncCheck("manual protocol redeems the selected far-future or undated credit with automatic mode off", async () => {
    var account = UsageParser.Hash(JsonSerializer.Serialize(new { identity = "test@example.com", workspace = (JsonElement?)null }));
    foreach (var expiry in new DateTimeOffset?[] { DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds()), null }) {
        var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = expiry?.ToUnixTimeSeconds().ToString() ?? "null";
        var sending = false; var result = await CodexClient.Consume(start, TimeSpan.FromSeconds(10), new(account, "fake-credit", expiry, "test-key", Manual: true, OnSending: () => sending = true));
        Assert(result.Outcome == "reset" && sending);
    }
});
await AsyncCheck("manual protocol validates selected credit and cancellation immediately before sending", async () => {
    var account = UsageParser.Hash(JsonSerializer.Serialize(new { identity = "test@example.com", workspace = (JsonElement?)null }));
    var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds());
    foreach (var scenario in new[] { "account", "id", "expiry", "duplicate", "status", "type", "cancel", "invalid-expiry", "empty-key" }) {
        var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = scenario == "invalid-expiry" ? "1e100" : expiry.ToUnixTimeSeconds().ToString();
        if (scenario == "duplicate") start.Environment["FAKE_DUPLICATE"] = "1";
        if (scenario == "status") start.Environment["FAKE_STATUS"] = "redeemed";
        if (scenario == "type") start.Environment["FAKE_TYPE"] = "other";
        var sending = false;
        var request = new ConsumeRequest(scenario == "account" ? "other-account" : account, scenario == "id" ? "other-credit" : "fake-credit", scenario == "expiry" ? expiry.AddSeconds(1) : scenario == "invalid-expiry" ? null : expiry, scenario == "empty-key" ? "" : "test-key", () => scenario != "cancel", Manual: true, OnSending: () => sending = true);
        try { await CodexClient.Consume(start, TimeSpan.FromSeconds(10), request); throw new Exception("Accepted " + scenario); } catch (PaceException) { }
        Assert(!sending, "Sent despite " + scenario);
    }
});
await AsyncCheck("manual protocol preserves server outcomes and never turns a disconnected result into success", async () => {
    var account = UsageParser.Hash(JsonSerializer.Serialize(new { identity = "test@example.com", workspace = (JsonElement?)null }));
    var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds());
    foreach (var outcome in new[] { "reset", "nothingToReset", "noCredit", "alreadyRedeemed", "disconnect", "unexpected" }) {
        var start = FakeStart("consume"); start.Environment["FAKE_EXPIRY"] = expiry.ToUnixTimeSeconds().ToString(); start.Environment["FAKE_OUTCOME"] = outcome;
        var sending = false;
        try {
            var result = await CodexClient.Consume(start, TimeSpan.FromSeconds(10), new(account, "fake-credit", expiry, "test-key", Manual: true, OnSending: () => sending = true));
            Assert(outcome is not ("disconnect" or "unexpected") && result.Outcome == outcome);
        } catch (PaceException) { Assert(outcome is "disconnect" or "unexpected"); }
        Assert(sending);
    }
});
Check("automatic reset revocation is durable", () => { var root = Path.Combine(Path.GetTempPath(), "AgentPaceRevocation-" + Guid.NewGuid()); try { var storage = new Storage(root); storage.RevokeAutoReset(); Assert(new Storage(root).AutoResetRevoked); storage.ClearAutoResetRevocation(); Assert(!new Storage(root).AutoResetRevoked); } finally { if (Directory.Exists(root)) Directory.Delete(root, true); } });
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
        {
            var expiry = Environment.GetEnvironmentVariable("FAKE_EXPIRY");
            var c = new { id = "fake-credit", resetType = Environment.GetEnvironmentVariable("FAKE_TYPE") ?? "codexRateLimits", status = Environment.GetEnvironmentVariable("FAKE_STATUS") ?? "available", expiresAt = expiry is null or "null" ? (double?)null : double.Parse(expiry, System.Globalization.CultureInfo.InvariantCulture) };
            Console.WriteLine(mode == "consume" ? JsonSerializer.Serialize(new { id = 3, result = new { rateLimits = new { primary = new { usedPercent = 30, windowDurationMins = 10080, resetsAt = 1999999999 } }, rateLimitResetCredits = new { availableCount = 1, credits = Environment.GetEnvironmentVariable("FAKE_DUPLICATE") == "1" ? new[] { c, c } : [c] } } }) : "{\"id\":3,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":30,\"windowDurationMins\":10080,\"resetsAt\":1999999999}}}}");
        }
        else if (method == "account/rateLimitResetCredit/consume")
        {
            var p = m.Get("params");
            if (mode != "consume" || p?.Str("creditId") != "fake-credit" || p?.Str("idempotencyKey") != "test-key") Environment.Exit(6);
            var outcome = Environment.GetEnvironmentVariable("FAKE_OUTCOME") ?? "reset";
            if (outcome == "disconnect") return;
            Console.WriteLine(JsonSerializer.Serialize(new { id = 4, result = new { outcome } }));
            return;
        }
        else if (method == "account/usage/read")
        {
            Console.WriteLine(mode == "old" ? "{\"id\":4,\"error\":{\"code\":-32601}}" : JsonSerializer.Serialize(new { id = 4, result = new { dailyUsageBuckets = new[] { new { startDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), tokens = 100 }, new { startDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), tokens = 50 } }, summary = new { lifetimeTokens = 150, peakDailyTokens = 100 } } }));
            return;
        }
        else if (method != "initialized")
            Environment.Exit(5);
    }
}
