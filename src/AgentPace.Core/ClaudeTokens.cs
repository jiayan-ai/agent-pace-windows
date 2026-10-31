using System.Text.Json;
namespace AgentPace.Core;

public sealed record TokenCounts(long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
    public static TokenCounts Zero => new(0, 0, 0, 0);
    public TokenCounts Add(TokenCounts other) => new(Input + other.Input, Output + other.Output, CacheWrite + other.CacheWrite, CacheRead + other.CacheRead);
    public TokenCounts Max(TokenCounts other) => new(Math.Max(Input, other.Input), Math.Max(Output, other.Output), Math.Max(CacheWrite, other.CacheWrite), Math.Max(CacheRead, other.CacheRead));
}
public sealed record ClaudeTokenSnapshot(DateTimeOffset FetchedAt, string SourceRoot, string TimeZoneId, string Day,
    TokenCounts? Today, TokenCounts? Last30Days, int RecordedDays, int Messages, bool Incomplete);

// Local Code transcripts are independent of the account connected in WebView.
// Read usage fields only; never retain prompts, responses or authentication data.
public static class ClaudeTokens
{
    public static string DefaultRoot => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
        ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
    public static Task<ClaudeTokenSnapshot> ReadAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.Run(() => Read(DefaultRoot, now, TimeZoneInfo.Local, ct), ct);
    public static ClaudeTokenSnapshot Read(string root, DateTimeOffset now, TimeZoneInfo? zone = null, CancellationToken ct = default)
    {
        zone ??= TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var start = today.AddDays(-29);
        var records = new Dictionary<string, (DateOnly Day, TokenCounts Counts)>(StringComparer.Ordinal);
        var incomplete = false;
        var examined = 0;
        long bytes = 0;
        var directories = new Stack<string>();
        if (Directory.Exists(root)) directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            string[] children;
            try { children = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { incomplete = true; continue; }
            foreach (var path in children.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                    if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    var length = new FileInfo(path).Length;
                    if (++examined > 10000 || length > 64L * 1024 * 1024 || (bytes += length) > 256L * 1024 * 1024)
                    { incomplete = true; continue; }
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (line.Length > 4_000_000) { incomplete = true; continue; }
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var record = document.RootElement;
                            if (record.Str("type") != "assistant" || record.Str("entrypoint") == "local-agent" ||
                                record.Get("isApiErrorMessage") is { ValueKind: JsonValueKind.True } ||
                                record.Get("message") is not { } message || message.Str("model") == "<synthetic>" ||
                                message.Get("usage") is not { ValueKind: JsonValueKind.Object } usage) continue;
                            if (Json.Date(record.Str("timestamp")) is not { } timestamp || timestamp > now)
                            { incomplete = true; continue; }
                            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
                            if (date < start || date > today) continue;
                            var requestId = record.Str("requestId");
                            if (message.Str("id") is not { Length: > 0 } id ||
                                (!string.IsNullOrEmpty(requestId) ? requestId : record.Str("sessionId")) is not { Length: > 0 } request)
                            { incomplete = true; continue; }
                            long? Count(string key) => usage.Num(key) is { } n && n >= 0 && n <= 9_007_199_254_740_991 && n == Math.Truncate(n) ? (long)n : null;
                            var input = Count("input_tokens"); var output = Count("output_tokens");
                            var write = Count("cache_creation_input_tokens"); var read = Count("cache_read_input_tokens");
                            if (input is null || output is null || write is null || read is null) { incomplete = true; continue; }
                            var counts = new TokenCounts(input.Value, output.Value, write.Value, read.Value);
                            if (counts.Total > 9_007_199_254_740_991) { incomplete = true; continue; }
                            var key = JsonSerializer.Serialize(new[] { string.IsNullOrEmpty(requestId) ? "session" : "request", request, id });
                            if (records.TryGetValue(key, out var previous))
                            {
                                if (previous.Day != date) incomplete = true;
                                records[key] = (previous.Day < date ? previous.Day : date, previous.Counts.Max(counts));
                            }
                            else records[key] = (date, counts);
                        }
                        catch (JsonException) { incomplete = true; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { incomplete = true; }
            }
        }
        var todayRecords = records.Values.Where(r => r.Day == today).ToList();
        TokenCounts? Sum(IEnumerable<(DateOnly Day, TokenCounts Counts)> items)
        {
            TokenCounts? total = null;
            foreach (var item in items)
            {
                if (item.Counts.Total > 9_007_199_254_740_991 - (total?.Total ?? 0)) { incomplete = true; continue; }
                total = (total ?? TokenCounts.Zero).Add(item.Counts);
            }
            return total;
        }
        return new(now, root, zone.Id, today.ToString("yyyy-MM-dd"), Sum(todayRecords), Sum(records.Values), records.Values.Select(r => r.Day).Distinct().Count(), records.Count, incomplete);
    }
}
