namespace AgentPace.Core;

// This journal contains only account hashes, opaque credit IDs and operation keys.
public sealed class AutoResetJournal
{
    public List<AutoResetAttempt> Attempts { get; set; } = [];
}
public sealed record AutoResetAttempt(string AccountKey, string CreditId, DateTimeOffset? Expiry, string IdempotencyKey, DateTimeOffset StartedAt, string? Outcome = null, bool Manual = false);
public static class AutoReset
{
    public static ResetCredit? Due(Snapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.AccountKey) || now < snapshot.FetchedAt || now - snapshot.FetchedAt > TimeSpan.FromMinutes(1) || snapshot.Credits is not { AvailableCount: > 0, Credits: { } credits })
            return null;
        return credits.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && c.Status == "available" && c.ResetType == "codexRateLimits" && c.Expiry is { } e && e > now && e - now <= TimeSpan.FromMinutes(5) && credits.Count(x => x is not null && x.Id == c.Id) == 1)
            .OrderBy(c => c.Expiry).FirstOrDefault();
    }
    public static bool IsEligible(Snapshot snapshot, string account, string id, DateTimeOffset? expiry, DateTimeOffset now)
    {
        var credit = Due(snapshot, now);
        return credit is not null && snapshot.AccountKey == account && credit.Id == id && credit.Expiry == expiry;
    }
    public static AutoResetAttempt? Prepare(AutoResetJournal journal, Snapshot snapshot, DateTimeOffset now)
    {
        var credit = Due(snapshot, now);
        if (credit?.Expiry is not { } expiry) return null;
        var existing = journal.Attempts.FirstOrDefault(a => a.AccountKey == snapshot.AccountKey && a.CreditId == credit.Id);
        if (existing is not null) return !existing.Manual && existing.Outcome is null && existing.Expiry == expiry ? existing : null;
        // A previous uncertain attempt may already have spent a credit. Do not
        // attempt another one until the user has checked and resolved it.
        if (journal.Attempts.Any(a => a.AccountKey == snapshot.AccountKey && a.Outcome is null)) return null;
        // One action per five-minute window: simultaneously expiring credits cannot
        // be spent back-to-back on the same quota reset.
        if (journal.Attempts.Any(a => a.AccountKey == snapshot.AccountKey && (a.StartedAt > now || now - a.StartedAt < TimeSpan.FromMinutes(5)))) return null;
        var attempt = new AutoResetAttempt(snapshot.AccountKey, credit.Id, expiry, Guid.NewGuid().ToString(), now);
        journal.Attempts.Add(attempt);
        return attempt;
    }
}

public static class ManualReset
{
    public static ResetCredit? Eligible(Snapshot? snapshot, string account, string id, DateTimeOffset? expiry, DateTimeOffset now)
    {
        if (snapshot is null || now < snapshot.FetchedAt || now - snapshot.FetchedAt > TimeSpan.FromMinutes(1))
            return null;
        return Available(snapshot, account, id, expiry, now);
    }

    public static ResetCredit? Available(Snapshot? snapshot, string account, string id, DateTimeOffset? expiry, DateTimeOffset now)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(account) || snapshot.AccountKey != account || string.IsNullOrWhiteSpace(id) ||
            snapshot.Credits is not { AvailableCount: > 0, Credits: { } credits } || credits.Count(c => c is not null && c.Id == id) != 1) return null;
        return credits.FirstOrDefault(c => c is not null && c.Id == id && c.Status == "available" && c.ResetType == "codexRateLimits" &&
            (c.ExpiresAt is null && expiry is null || c.Expiry is { } end && end == expiry && end > now));
    }

    public static AutoResetAttempt? Prepare(AutoResetJournal journal, Snapshot snapshot, string account, string id, DateTimeOffset? expiry, DateTimeOffset now)
    {
        if (Eligible(snapshot, account, id, expiry, now) is null) return null;
        var existing = journal.Attempts.LastOrDefault(a => a.AccountKey == account && a.CreditId == id);
        // A deliberate retry must use the original key, including an uncertain
        // automatic operation. Only a confirmed no-op permits a new operation
        // after another user confirmation and a fresh account check.
        if (existing is { Outcome: null }) return existing.Expiry == expiry ? existing : null;
        if (existing is not null && (existing.Outcome is not ("nothingToReset" or "noCredit") || snapshot.FetchedAt <= existing.StartedAt)) return null;
        if (journal.Attempts.Any(a => a.AccountKey == account && (a.Outcome is null || a.StartedAt > now || a.CreditId != id && now - a.StartedAt < TimeSpan.FromMinutes(5)))) return null;
        var attempt = new AutoResetAttempt(account, id, expiry, Guid.NewGuid().ToString(), now, Manual: true);
        journal.Attempts.Add(attempt);
        return attempt;
    }
}
