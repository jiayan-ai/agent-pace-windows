using System.Windows.Threading;
namespace AgentPace.Windows;

public sealed class Store : IDisposable
{
    public event Action? Changed;
    public Settings Settings { get; private set; } = new();
    public History History { get; private set; } = new();
    public RadarSnapshot? Radar
    {
        get; private set;
    }
    public bool RadarFailed
    {
        get; private set;
    }
    public bool RadarBusy
    {
        get; private set;
    }
    public string? PersistenceError
    {
        get; private set;
    }
    public readonly Storage Storage;
    public ClaudeSession Claude
    {
        get;
    }
    readonly Dictionary<string, PaceException> errors = [];
    readonly bool demo;
    AutoResetJournal autoJournal;
    bool autoJournalValid = true, autoBusy;
    DateTimeOffset autoLastAttempt = DateTimeOffset.MinValue;
    string autoResetStatus = "";
    string? CurrentResetAccount => History.For("codex").Snapshot?.AccountKey;
    public bool HasPendingAutoReset => autoJournal.Attempts.Any(a => a.AccountKey == CurrentResetAccount && a.Outcome is null);
    public bool ResetBusy => autoBusy;
    public bool IsPendingReset(string account, string creditId) => autoJournal.Attempts.Any(a => a.AccountKey == account && a.CreditId == creditId && a.Outcome is null);
    public string AutoResetStatus { get => !autoJournalValid ? L.T("操作记录无法读取或保存，Reset 使用已暂停。", "Operation journal unavailable; redemption paused.") : HasPendingAutoReset && !autoBusy ? L.T("上次操作结果未确认。可重试同一张券；使用其他券前，请先在 Codex 核对并解除暂停。", "Previous result is unconfirmed. You may retry the same credit; check Codex and resolve it before using another.") : autoResetStatus; private set => autoResetStatus = value; }
    readonly HashSet<string> busy = [], verified = [];
    readonly Dictionary<string, DateTimeOffset> next = [], attempted = [];
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    DateTimeOffset radarNext = DateTimeOffset.MinValue, radarAttempt = DateTimeOffset.MinValue;
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;
    public string Provider => History.SelectedProvider;
    public ProviderHistory Current => History.For(Provider);
    public bool Busy => busy.Contains(Provider);
    public bool Stale => !verified.Contains(Provider) || errors.ContainsKey(Provider) || Current.Snapshot is not { } s || (Now - s.FetchedAt).TotalSeconds is < 0 or > 600 || Provider == "codex" && HasExpiredCredit(Current, Now);
    static bool HasExpiredCredit(ProviderHistory history, DateTimeOffset now)
    {
        var selected = history.Snapshot?.Windows.FirstOrDefault(w => w.Id == history.SelectedId) ?? history.Snapshot?.Windows.FirstOrDefault();
        return AgentPace.Core.CreditPace.HasExpiredCreditRequiringConfirmation(history.Snapshot, selected, now);
    }
    public bool CreditExpired => Provider == "codex" && HasExpiredCredit(Current, Now);
    public PaceException? Error => errors.GetValueOrDefault(Provider);
    public MeteredWindow? Selected => Current.Snapshot?.Windows.FirstOrDefault(w => w.Id == Current.SelectedId) ?? Current.Snapshot?.Windows.FirstOrDefault();
    public SessionPace? Session => Provider == "claude" && Selected?.Window.WindowDurationMins == 300 ? new(Selected.Window, Now) : null;
    public CreditPace? CreditPace => Provider == "codex" ? AgentPace.Core.CreditPace.Evaluate(Current.Snapshot, Selected, Stale, Now) : null;
    public bool CreditNeedsConfirmation => Provider == "codex" && AgentPace.Core.CreditPace.NeedsConfirmation(Current.Snapshot, Selected, Stale, Now);
    public DailyBudget? Budget => Selected is { } w && w.Window.ResetDate > Now && Current.Snapshot is { } s && Current.Budgets.TryGetValue(s.AccountKey + "/" + w.Id, out var b) && b.Day == TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.Local).ToString("yyyy-MM-dd") && b.TimeZoneId == TimeZoneInfo.Local.Id ? b : null;
    public string Summary
    {
        get
        {
            var name = Selected?.BucketId == "claude_fable" ? "Fable" : Provider == "claude" ? "Claude" : "Codex";
            var mark = Stale ? "≈" : "";
            if (CreditNeedsConfirmation)
                return name + " " + L.T("待同步", "Pending");
            if (CreditPace is { } credit)
                return name + " " + (credit.Allowance is { } a ? $"{L.Number(a)}%" + (credit.FinalHour ? " · <1h" : "/h") : L.T("待同步", "Pending"));
            if (Session is { } session)
                return name + " " + (session.Allowance is { } a ? $"{mark}{L.Number(a)}%" + (session.FinalHour ? " · <1h" : "/h") : L.T("待同步", "Pending"));
            return name + " " + (Budget is { } b ? $"{mark}{L.Number(b.LeftToday)}%" : L.T("待同步", "Pending"));
        }
    }
    public Store(string? root = null, bool demo = false)
    {
        this.demo = demo;
        Storage = new(root ?? Storage.DefaultRoot);
        try
        {
            Settings = Storage.Read<Settings>("settings.json");
            History = Storage.Read<History>("state.json");
            if (History.Providers is null || History.Providers.Values.Any(h => h is null || h.Budgets is null || h.Budgets.Values.Any(b => b is null) || h.Snapshot is { } snap && (snap.Windows is null || snap.Windows.Any(w => w is null))))
                throw new InvalidDataException();
            if (History.SelectedProvider is not ("codex" or "claude"))
                History.SelectedProvider = "codex";
        }
        catch { try { Storage.PreserveUnreadable("settings.json"); Storage.PreserveUnreadable("state.json"); } catch { } Settings = new(); History = new(); PersistenceError = L.T("本地记录无法读取，已尝试保留原文件备份。", "Saved records could not be read; preserving a backup was attempted."); }
        try { autoJournal = Storage.Read<AutoResetJournal>("auto-reset-journal.json"); if (autoJournal.Attempts is null || autoJournal.Attempts.Any(a => a is null || string.IsNullOrWhiteSpace(a.IdempotencyKey))) throw new InvalidDataException(); }
        catch { autoJournal = new(); autoJournalValid = false; }
        if (Storage.AutoResetRevoked) Settings.AutoResetEnabled = false;
        L.Language = Settings.Language is "en" or "zh-Hans" ? Settings.Language : "system";
        try
        {
            var path = Path.Combine(Storage.Root, "reset-radar.json");
            if (File.Exists(path) && new FileInfo(path).Length < 1_048_576)
                Radar = System.Text.Json.JsonSerializer.Deserialize<RadarSnapshot>(File.ReadAllText(path), Json.Options);
        }
        catch { }
        Claude = new(this);
        timer.Tick += async (_, _) => { Now = DateTimeOffset.Now; Notify(); if (!demo) { await RefreshDue(); await AutoResetTick(); } };
        timer.Start();
        if (demo)
            LoadDemo();
    }
    public void Notify() => Changed?.Invoke();
    public void Save()
    {
        try
        {
            Storage.Write("settings.json", Settings);
            Storage.Write("state.json", History);
            PersistenceError = null;
        }
        catch { PersistenceError = L.T("本地记录无法保存；重启后可能重新计算预算。", "Local records could not be saved; a restart may recalculate budgets."); }
    }
    public void Language(string language)
    {
        Settings.Language = language;
        L.Language = language;
        Save();
        Claude.RefreshLanguage();
        ForecastWindow.UpdateLanguage();
        Notify();
    }
    public void SelectProvider(string provider)
    {
        History.SelectedProvider = provider;
        Save();
        Notify();
    }
    public void SelectWindow(string id)
    {
        Current.SelectedId = id;
        Save();
        Notify();
    }
    public async Task RefreshDue(bool force = false)
    {
        foreach (var p in new[] { "codex", "claude" })
        {
            var state = History.For(p);
            var expired = state.Snapshot is { } snapshot && (p == "claude" && snapshot.Windows.Any(w => w.Window.ResetDate <= Now) || p == "codex" && HasExpiredCredit(state, Now));
            if (force || !next.TryGetValue(p, out var due) || Now >= due || expired && errors.GetValueOrDefault(p)?.RetrySeconds is not > 0 && attempted.GetValueOrDefault(p) <= Now.AddMinutes(-1))
                _ = Refresh(p);
        }
        if (Settings.RadarEnabled && (force || Now >= radarNext))
            await RefreshRadar();
    }
    public async Task Refresh(string? provider = null, bool resetOwned = false)
    {
        provider ??= Provider;
        if (demo)
            return;
        if (busy.Contains(provider) || provider == "codex" && autoBusy && !resetOwned || provider == "claude" && (!Settings.ClaudeEnabled || errors.GetValueOrDefault(provider)?.NeedsLogin == true))
            return;
        if (errors.GetValueOrDefault(provider)?.RetrySeconds > 0 && next.GetValueOrDefault(provider) > DateTimeOffset.Now)
            return;
        busy.Add(provider);
        attempted[provider] = DateTimeOffset.Now;
        Notify();
        try
        {
            var snapshot = provider == "codex" ? await CodexClient.Fetch(new(Settings.CodexExecutable, Settings.WslDistribution), lifetime.Token) : await Claude.Fetch(lifetime.Token);
            if (provider == "claude" && !Settings.ClaudeEnabled)
                return;
            History.For(provider).Ingest(snapshot);
            verified.Add(provider);
            errors.Remove(provider);
            next[provider] = DateTimeOffset.Now.AddMinutes(5);
            Save();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { var error = ex as PaceException ?? PaceException.Network(); errors[provider] = error; next[provider] = DateTimeOffset.Now.AddSeconds(Math.Max(60, error.RetrySeconds)); }
        finally { busy.Remove(provider); Now = DateTimeOffset.Now; Notify(); }
    }
    public void SetAutoReset(bool enabled)
    {
        if (!enabled)
        {
            try
            {
                // Revocation is independent of the operation lock: it can stop an
                // in-flight attempt before its final authorization check.
                using var authorization = Storage.AcquireAutoResetAuthorizationLock();
                Storage.RevokeAutoReset();
                Settings.AutoResetEnabled = false;
                try { Storage.Write("settings.json", Settings); AutoResetStatus = ""; }
                catch { AutoResetStatus = L.T("自动使用已停用，但设置文件保存失败。", "Automatic redemption is disabled, but settings could not be saved."); }
            }
            catch { AutoResetStatus = L.T("无法保存关闭状态；请重试或退出应用。", "Could not persist disabling; retry or quit the app."); }
            Notify(); return;
        }
        var codex = History.For("codex").Snapshot;
        if (codex is null || !verified.Contains("codex") || errors.ContainsKey("codex") || DateTimeOffset.UtcNow < codex.FetchedAt || DateTimeOffset.UtcNow - codex.FetchedAt > TimeSpan.FromMinutes(5))
        { AutoResetStatus = L.T("请先成功同步 Codex 账户，再开启自动使用。", "Sync the Codex account successfully before enabling automatic redemption."); Notify(); return; }
        var oldEnabled = Settings.AutoResetEnabled;
        var oldAccount = Settings.AutoResetAccountKey;
        try
        {
            using var heldLock = Storage.AcquireAutoResetLock();
            using var authorization = Storage.AcquireAutoResetAuthorizationLock();
            Settings.AutoResetEnabled = true;
            Settings.AutoResetAccountKey = codex.AccountKey;
            Storage.Write("settings.json", Settings);
            Storage.ClearAutoResetRevocation();
            AutoResetStatus = "";
        }
        catch { Settings.AutoResetEnabled = oldEnabled; Settings.AutoResetAccountKey = oldAccount; AutoResetStatus = L.T("无法保存设置或另一个实例正在处理；请重试。", "Could not save the setting or another instance is processing; retry."); }
        Notify();
    }
    public void ResolvePendingAutoReset(string account)
    {
        if (autoBusy) return;
        if (string.IsNullOrWhiteSpace(account) || CurrentResetAccount != account)
        { AutoResetStatus = L.T("账户已变化，请重新核对当前账户的 Reset 结果。", "The account changed. Review the current account's reset result first."); Notify(); return; }
        try
        {
            using var heldLock = Storage.AcquireAutoResetLock();
            var journal = Storage.Read<AutoResetJournal>("auto-reset-journal.json");
            if (journal.Attempts is null || journal.Attempts.Any(a => a is null)) throw new InvalidDataException();
            for (var i = 0; i < journal.Attempts.Count; i++)
            {
                var a = journal.Attempts[i];
                if (a.AccountKey == account && a.Outcome is null)
                    journal.Attempts[i] = a with { Outcome = "manuallyReviewed" };
            }
            Storage.Write("auto-reset-journal.json", journal);
            autoJournal = journal;
            AutoResetStatus = L.T("已标记核对；原操作不会使用新编号重试。", "Marked as reviewed; the previous operation will not be retried with a new key.");
        }
        catch { AutoResetStatus = L.T("无法保存核对结果，仍处于暂停状态。", "Could not save review; automatic redemption remains paused."); }
        Notify();
    }

    public string? ManualResetUnavailable(string account, ResetCredit credit)
    {
        if (demo) return L.T("演示模式不使用真实券。", "Demo mode cannot redeem credits.");
        if (autoBusy || busy.Contains("codex")) return L.T("正在处理，请稍候。", "An operation is in progress; please wait.");
        if (!autoJournalValid) return L.T("操作记录不可用，暂时无法使用。", "Operation journal unavailable; redemption paused.");
        var snapshot = History.For("codex").Snapshot;
        var now = DateTimeOffset.UtcNow;
        if (!verified.Contains("codex") || errors.ContainsKey("codex") || snapshot is null || snapshot.FetchedAt > now || now - snapshot.FetchedAt > TimeSpan.FromMinutes(10))
            return L.T("请先刷新 Codex 额度。", "Refresh Codex usage first.");
        if (ManualReset.Available(snapshot, account, credit.Id, credit.Expiry, now) is null)
            return L.T("这张券的状态、编号或有效期无法确认，请刷新。", "This credit's status, ID or expiry is unconfirmed. Refresh first.");
        var existing = autoJournal.Attempts.LastOrDefault(a => a.AccountKey == account && a.CreditId == credit.Id);
        if (existing is { Outcome: null }) return existing.Expiry == credit.Expiry ? null : L.T("有效期已变化，请先核对上次操作。", "Expiry changed; review the previous operation first.");
        if (existing is not null && existing.Outcome is not ("nothingToReset" or "noCredit"))
            return L.T("这张券已处理，不会再次提交。", "This credit was already handled and will not be resubmitted.");
        if (autoJournal.Attempts.Any(a => a.AccountKey == account && a.Outcome is null))
            return L.T("请先核对上次 Reset 的结果。", "Resolve the previous reset first.");
        if (autoJournal.Attempts.Any(a => a.AccountKey == account && (a.StartedAt > now || a.CreditId != credit.Id && now - a.StartedAt < TimeSpan.FromMinutes(5))))
            return L.T("刚刚处理过另一张券，请间隔 5 分钟再试。", "Another credit was just handled; wait 5 minutes before using another.");
        return null;
    }

    // Confirmation captures an exact account, credit and expiry. The fresh read
    // below and the final app-server read must both match that captured choice.
    public async Task RedeemReset(string account, ResetCredit credit)
    {
        if (ManualResetUnavailable(account, credit) is { } reason) { AutoResetStatus = reason; Notify(); return; }
        autoBusy = true;
        autoLastAttempt = DateTimeOffset.UtcNow;
        AutoResetStatus = L.T("正在核对账户与所选 Reset 券…", "Checking the account and selected reset credit…");
        Notify();
        AutoResetAttempt? attempt = null;
        var newlyPrepared = false;
        var mayHaveSent = false;
        try
        {
            using var heldLock = Storage.AcquireAutoResetLock();
            try
            {
                autoJournal = Storage.Read<AutoResetJournal>("auto-reset-journal.json");
                if (autoJournal.Attempts is null || autoJournal.Attempts.Any(a => a is null || string.IsNullOrWhiteSpace(a.IdempotencyKey))) throw new InvalidDataException();
            }
            catch { autoJournalValid = false; AutoResetStatus = L.T("无法读取操作记录，未发送使用请求。", "Cannot read operation journal; no redemption request was sent."); return; }
            var requestedAt = DateTimeOffset.UtcNow;
            await Refresh("codex", resetOwned: true);
            var snapshot = History.For("codex").Snapshot;
            if (!verified.Contains("codex") || errors.ContainsKey("codex") || snapshot is null || snapshot.FetchedAt < requestedAt ||
                ManualReset.Eligible(snapshot, account, credit.Id, credit.Expiry, DateTimeOffset.UtcNow) is null)
            { AutoResetStatus = L.T("同步失败，或账户、券状态已变化；未使用，请刷新后重新选择。", "Sync failed or the account/credit changed. No redemption was sent; refresh and choose again."); return; }
            var previous = autoJournal.Attempts.ToArray();
            attempt = ManualReset.Prepare(autoJournal, snapshot, account, credit.Id, credit.Expiry, DateTimeOffset.UtcNow);
            if (attempt is null) { AutoResetStatus = L.T("这张券已处理、其他操作待确认，或仍在 5 分钟保护间隔内。", "This credit was handled, another result is pending, or the 5-minute protection interval is active."); return; }
            newlyPrepared = !previous.Contains(attempt);
            try { Storage.Write("auto-reset-journal.json", autoJournal); }
            catch { autoJournalValid = false; AutoResetStatus = L.T("无法保存操作记录，未发送使用请求。", "Cannot save operation journal; no redemption request was sent."); return; }
            AutoResetStatus = L.T("正在使用所选 Reset 券…", "Redeeming the selected reset credit…"); Notify();
            var request = new ConsumeRequest(account, credit.Id, credit.Expiry, attempt.IdempotencyKey,
                () => !lifetime.IsCancellationRequested && CurrentResetAccount == account,
                () => Storage.AcquireAutoResetAuthorizationLock(), Manual: true, OnSending: () => mayHaveSent = true);
            CodexResult result;
            try { result = await CodexClient.Consume(new(Settings.CodexExecutable, Settings.WslDistribution), request, lifetime.Token); }
            catch
            {
                // If the process failed before its final send, a new attempt can
                // be discarded safely. An older uncertain operation stays pending.
                if (!mayHaveSent && newlyPrepared)
                {
                    autoJournal.Attempts.Remove(attempt);
                    try { Storage.Write("auto-reset-journal.json", autoJournal); }
                    catch { autoJournalValid = false; }
                }
                AutoResetStatus = mayHaveSent || !newlyPrepared
                    ? L.T("使用结果未确认。请在 Codex 核对，或重试同一张券；不会自动换券。", "Redemption result is unconfirmed. Check Codex or retry the same credit; another credit will not be substituted.")
                    : L.T("发送前核验失败，未使用 Reset；请刷新后重试。", "Verification failed before sending. No reset was used; refresh and retry.");
                await Refresh("codex", resetOwned: true);
                return;
            }
            var index = autoJournal.Attempts.IndexOf(attempt);
            if (index >= 0) autoJournal.Attempts[index] = attempt with { Outcome = result.Outcome };
            try { Storage.Write("auto-reset-journal.json", autoJournal); }
            catch
            {
                autoJournalValid = false;
                AutoResetStatus = L.T("结果无法保存，请在 Codex 核对；Reset 使用已暂停。", "Could not save the result. Check Codex; further redemption is paused.");
                // Keep redemption paused, but still read the quota after a confirmed result.
                await Refresh("codex", resetOwned: true);
                return;
            }
            AutoResetStatus = result.Outcome switch
            {
                "reset" => L.T("Reset 券已使用，额度已请求刷新。", "Reset credit used; quota refresh requested."),
                "alreadyRedeemed" => L.T("这张 Reset 券此前已使用，正在刷新额度。", "This reset credit was already redeemed. Refreshing quota."),
                "nothingToReset" => L.T("未使用：当前没有可重置的额度。之后有用量时可再点击。", "Not used: there is nothing to reset. You can try again after using quota."),
                "noCredit" => L.T("未使用：服务端没有可用的这张券，已请求刷新。", "Not used: the server reports no available credit. Refresh requested."),
                _ => L.T("结果未确认，请在 Codex 核对。", "Result unconfirmed. Check Codex.")
            };
            await Refresh("codex", resetOwned: true);
            if (errors.ContainsKey("codex")) AutoResetStatus += L.T(" 额度刷新失败，请稍后刷新查看。", " Quota refresh failed; refresh again later.");
        }
        catch { AutoResetStatus = mayHaveSent
            ? L.T("使用结果未确认，请在 Codex 核对后再继续。", "Redemption result is unconfirmed. Check Codex before continuing.")
            : L.T("无法锁定操作记录或完成核验，未发送使用请求。", "Could not lock the operation journal or finish verification; no redemption request was sent."); }
        finally { autoBusy = false; Now = DateTimeOffset.Now; Notify(); }
    }
    bool AutoResetStillEnabled(string account)
    {
        if (!Settings.AutoResetEnabled || Settings.AutoResetAccountKey != account || Storage.AutoResetRevoked) return false;
        try { var saved = Storage.Read<Settings>("settings.json"); return saved.AutoResetEnabled && saved.AutoResetAccountKey == account; }
        catch { return false; }
    }
    async Task AutoResetTick()
    {
        if (!Settings.AutoResetEnabled || Storage.AutoResetRevoked || autoBusy || busy.Contains("codex") || !autoJournalValid || DateTimeOffset.UtcNow - autoLastAttempt < TimeSpan.FromMinutes(1)) return;
        var cached = History.For("codex").Snapshot;
        var now = DateTimeOffset.UtcNow;
        if (cached is null) { AutoResetStatus = L.T("等待同步 Codex 账户。", "Waiting for Codex account sync."); Notify(); return; }
        if (cached.AccountKey != Settings.AutoResetAccountKey)
        { SetAutoReset(false); if (!Settings.AutoResetEnabled) AutoResetStatus = L.T("Codex 账户已变化；自动使用已关闭，请重新开启。", "Codex account changed; automatic redemption is off. Enable it again if desired."); Notify(); return; }
        if (cached?.Credits is not { AvailableCount: > 0, Credits: { } credits } || !credits.Any(c => c is not null && c.Status == "available" && c.ResetType == "codexRateLimits" && c.Expiry is { } expiry && expiry > now && expiry - now <= TimeSpan.FromMinutes(5))) return;
        autoBusy = true;
        autoLastAttempt = now;
        try
        {
            FileStream operationLock;
            try { operationLock = Storage.AcquireAutoResetLock(); }
            catch { AutoResetStatus = L.T("另一个实例正在处理或无法锁定记录，本次未发送。", "Another instance is processing or the journal cannot be locked; no request was sent."); return; }
            using var heldLock = operationLock;
            // Reload while holding the cross-process lock; another process may have
            // recorded this credit since this instance launched.
            try { var loaded = Storage.Read<AutoResetJournal>("auto-reset-journal.json"); if (loaded.Attempts is null || loaded.Attempts.Any(a => a is null || string.IsNullOrWhiteSpace(a.IdempotencyKey))) throw new InvalidDataException(); autoJournal = loaded; }
            catch { autoJournalValid = false; AutoResetStatus = L.T("无法读取操作记录，自动使用已暂停。", "Cannot read operation journal; automatic redemption paused."); return; }
            AutoResetStatus = L.T("正在确认账户与临期券…", "Checking account and expiring credit…"); Notify();
            await Refresh("codex", resetOwned: true);
            var snapshot = History.For("codex").Snapshot;
            if (!AutoResetStillEnabled(cached.AccountKey)) return;
            if (snapshot is not null && snapshot.AccountKey != cached.AccountKey)
            { SetAutoReset(false); if (!Settings.AutoResetEnabled) AutoResetStatus = L.T("Codex 账户已变化；自动使用已关闭，请重新开启。", "Codex account changed; automatic redemption is off. Enable it again if desired."); return; }
            if (!verified.Contains("codex") || errors.ContainsKey("codex") || snapshot is null || snapshot.AccountKey != Settings.AutoResetAccountKey || snapshot.FetchedAt < now)
            { AutoResetStatus = L.T("账户同步失败，未使用；请检查登录和网络。", "Account sync failed; no reset was sent. Check sign-in and network."); return; }
            var attempt = AutoReset.Prepare(autoJournal, snapshot, DateTimeOffset.UtcNow);
            if (attempt is null) { AutoResetStatus = L.T("暂无可确认的临期券，或本次已处理。", "No confirmed expiring credit, or this window was already handled."); return; }
            // The journal must reach disk before the server can consume a credit.
            try { Storage.Write("auto-reset-journal.json", autoJournal); }
            catch { autoJournalValid = false; AutoResetStatus = L.T("无法保存操作记录，自动使用已暂停。", "Cannot save operation journal; automatic redemption paused."); return; }
            AutoResetStatus = L.T("正在使用临期 Reset 券…", "Redeeming expiring reset credit…"); Notify();
            var request = new ConsumeRequest(attempt.AccountKey, attempt.CreditId, attempt.Expiry, attempt.IdempotencyKey, () => AutoResetStillEnabled(attempt.AccountKey), () => Storage.AcquireAutoResetAuthorizationLock());
            var result = await CodexClient.Consume(new(Settings.CodexExecutable, Settings.WslDistribution), request, lifetime.Token);
            var index = autoJournal.Attempts.IndexOf(attempt);
            if (index >= 0) autoJournal.Attempts[index] = attempt with { Outcome = result.Outcome };
            try { Storage.Write("auto-reset-journal.json", autoJournal); }
            catch
            {
                autoJournalValid = false;
                AutoResetStatus = L.T("结果记录无法保存，自动使用已暂停；请在 Codex 确认。", "Could not save result; automatic redemption paused. Check Codex.");
                await Refresh("codex", resetOwned: true);
                return;
            }
            AutoResetStatus = result.Outcome switch {
                "reset" => L.T("临期 Reset 券已使用。", "Expiring reset credit used."),
                "alreadyRedeemed" => L.T("该 Reset 券已使用，请刷新确认额度。", "This credit was already redeemed. Refresh to confirm quota."),
                "nothingToReset" => L.T("未使用：当前没有可重置的额度。", "Not used: there is nothing to reset."),
                "noCredit" => L.T("未使用：账户没有可用 Reset 券。", "Not used: no reset credit is available."),
                _ => L.T("结果未知，请在 Codex 确认。", "Result unknown. Check Codex.")
            };
            await Refresh("codex", resetOwned: true);
        }
        catch (OperationCanceledException) { }
        catch { AutoResetStatus = L.T("结果未确认：请检查登录与网络；若仍在有效期内会使用同一操作编号重试。", "Result unconfirmed. Check sign-in and network; retries use the same operation key while the credit remains valid."); }
        finally { autoBusy = false; Notify(); }
    }
    public async Task ConnectClaude()
    {
        if (demo)
            return;
        Settings.ClaudeEnabled = true;
        if (errors.GetValueOrDefault("claude")?.NeedsLogin == true)
            errors.Remove("claude");
        Save();
        try
        {
            await Claude.ShowLogin();
            await Refresh("claude");
        }
        catch { errors["claude"] = new("无法打开登录窗口。请安装 Microsoft Edge WebView2 Runtime 后重试。", "Could not open sign-in. Install Microsoft Edge WebView2 Runtime and retry.", true); Notify(); }
    }
    public async Task SyncClaude()
    {
        if (errors.GetValueOrDefault("claude")?.NeedsLogin == true)
            errors.Remove("claude");
        await Refresh("claude");
    }
    public async Task DisconnectClaude()
    {
        Settings.ClaudeEnabled = false;
        Settings.ClaudeOrganization = "";
        verified.Remove("claude");
        errors.Remove("claude");
        History.Providers.Remove("claude");
        Save();
        Notify();
        try
        {
            await Claude.Disconnect();
        }
        catch { errors["claude"] = new("同步已停止，但登录会话清理失败，请重试断开。", "Sync stopped, but session cleanup failed. Retry disconnecting."); Notify(); }
    }
    public async Task RefreshRadar()
    {
        if (demo || !Settings.RadarEnabled || RadarBusy || DateTimeOffset.Now < radarAttempt.AddMinutes(1))
            return;
        RadarBusy = true;
        radarAttempt = DateTimeOffset.Now;
        Notify();
        try
        {
            var value = await RadarClient.Fetch(lifetime.Token);
            if (Settings.RadarEnabled)
            {
                Radar = value;
                RadarFailed = false;
                Storage.Write("reset-radar.json", value);
            }
        }
        catch { RadarFailed = true; }
        finally { RadarBusy = false; radarNext = DateTimeOffset.Now.AddMinutes(15); Notify(); }
    }
    public void LoadDemo()
    {
        var now = Now;
        var end = now.AddDays(6).ToUnixTimeSeconds();
        History.For("codex").Ingest(new("demo-codex", now, [new("codex", "Codex", "primary", new(17, 10080, end))], new(2, [])));
        History.For("claude").Ingest(new("demo-claude", now, [new("claude", "Claude", "primary", new(33, 10080, end)), new("claude", "Claude", "secondary", new(40, 300, now.AddHours(3).ToUnixTimeSeconds())), new("claude_fable", "Claude Fable", "primary", new(42, 10080, end))]));
        verified.UnionWith(["codex", "claude"]);
    }
    public void Dispose()
    {
        timer.Stop();
        lifetime.Cancel();
        Claude.Dispose();
        lifetime.Dispose();
    }
}
