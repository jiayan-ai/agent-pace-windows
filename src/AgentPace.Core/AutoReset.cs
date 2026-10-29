namespace AgentPace.Core;

// This journal contains only account hashes, opaque credit IDs and operation keys.
public sealed class AutoResetJournal
{
    public List<AutoResetAttempt> Attempts { get; set; } = [];
}
public sealed record AutoResetAttempt(string AccountKey, string CreditId, DateTimeOffset Expiry, string IdempotencyKey, DateTimeOffset StartedAt, string? Outcome = null);
public static class AutoReset
{
    public static ResetCredit? Due(Snapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.AccountKey) || now < snapshot.FetchedAt || now - snapshot.FetchedAt > TimeSpan.FromMinutes(1) || snapshot.Credits is not { AvailableCount: > 0, Credits: { } credits })
            return null;
        return credits.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && c.Status == "available" && c.ResetType == "codexRateLimits" && c.Expiry is { } e && e > now && e - now <= TimeSpan.FromMinutes(5) && credits.Count(x => x is not null && x.Id == c.Id) == 1)
            .OrderBy(c => c.Expiry).FirstOrDefault();
    }
    public static bool IsEligible(Snapshot snapshot, string account, string id, DateTimeOffset expiry, DateTimeOffset now)
    {
        var credit = Due(snapshot, now);
        return credit is not null && snapshot.AccountKey == account && credit.Id == id && credit.Expiry == expiry;
    }
    public static AutoResetAttempt? Prepare(AutoResetJournal journal, Snapshot snapshot, DateTimeOffset now)
    {
        var credit = Due(snapshot, now);
        if (credit?.Expiry is not { } expiry) return null;
        var existing = journal.Attempts.FirstOrDefault(a => a.AccountKey == snapshot.AccountKey && a.CreditId == credit.Id);
        if (existing is not null) return existing.Outcome is null && existing.Expiry == expiry ? existing : null;
        // A previous uncertain attempt may already have spent a credit. Do not
        // attempt another one until the user has checked and resolved it.
        if (journal.Attempts.Any(a => a.AccountKey == snapshot.AccountKey && a.Outcome is null)) return null;
        // One action per five-minute window: simultaneously expiring credits cannot
        // be spent back-to-back on the same quota reset.
        if (journal.Attempts.Any(a => a.AccountKey == snapshot.AccountKey && a.StartedAt <= now && now - a.StartedAt < TimeSpan.FromMinutes(5))) return null;
        var attempt = new AutoResetAttempt(snapshot.AccountKey, credit.Id, expiry, Guid.NewGuid().ToString(), now);
        journal.Attempts.Add(attempt);
        return attempt;
    }
}
