using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace AgentPace.Core;

public sealed record CodexOptions(string? Executable = null, string? WslDistribution = null);
public static class CodexClient
{
    public static string? FindExecutable()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "codex.exe"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".local", "bin", "codex.exe"));
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
        foreach (var pkg in new[] { "codex", "codex-win32-x64", "codex-win32-arm64" })
            foreach (var arch in new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" })
            {
                candidates.Add(Path.Combine(npm, pkg, "vendor", arch, "codex", "codex.exe"));
                candidates.Add(Path.Combine(npm, "codex", "node_modules", "@openai", pkg, "vendor", arch, "codex", "codex.exe"));
            }
        return candidates.FirstOrDefault(File.Exists);
    }
    public static ProcessStartInfo StartInfo(CodexOptions options)
    {
        ProcessStartInfo start;
        if (!string.IsNullOrWhiteSpace(options.WslDistribution))
        {
            start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe"));
            foreach (var arg in new[] { "--distribution", options.WslDistribution, "--exec", "codex", "app-server", "--listen", "stdio://" })
                start.ArgumentList.Add(arg);
        }
        else
        {
            var path = string.IsNullOrWhiteSpace(options.Executable) ? FindExecutable() : options.Executable;
            if (path is null || !File.Exists(path) || !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new PaceException("未找到 codex.exe，请安装 Codex CLI，或在设置中选择可执行文件。", "codex.exe was not found. Install Codex CLI or select its executable in Settings.");
            start = new(path);
            foreach (var arg in new[] { "app-server", "--listen", "stdio://" })
                start.ArgumentList.Add(arg);
        }
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return start;
    }
    public static Task<Snapshot> Fetch(CodexOptions options, CancellationToken ct = default) => Fetch(StartInfo(options), TimeSpan.FromSeconds(35), ct);
    // This overload permits protocol integration tests against a child-process fake.
    public static async Task<Snapshot> Fetch(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        Snapshot? quota = null;
        try
        {
            if (!process.Start())
                throw PaceException.Network();
            // Drain, discard and never log stderr (which can contain account details).
            var drain = Drain(process.StandardError, lifetime.Token);
            var input = process.StandardInput;
            var output = process.StandardOutput;
            async Task Send(object value) => await input.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), lifetime.Token);
            // Use compact serialization: JSON-RPC messages must occupy one line.
            await Send(new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new
                    {
                        name = "agent_pace_windows",
                        title = "Agent Pace",
                        version = "1.0.0"
                    }
                }
            });
            string? account = null;
            var historyDeadline = DateTimeOffset.MaxValue;
            while (!lifetime.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (quota is not null)
                    readTimeout.CancelAfter(historyDeadline - DateTimeOffset.UtcNow > TimeSpan.Zero ? historyDeadline - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(1));
                string? line;
                try
                {
                    line = await ReadBoundedLine(output, readTimeout.Token);
                }
                catch (OperationCanceledException) when (quota is not null && !ct.IsCancellationRequested) { return quota; }
                if (line is null)
                {
                    if (quota is not null)
                        return quota;
                    throw PaceException.Network();
                }
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var m = doc.RootElement;
                    if (m.Get("method") is not null)
                    {
                        if (m.Get("id") is { } requestId)
                            await Send(new
                            {
                                id = requestId,
                                error = new
                                {
                                    code = -32601,
                                    message = "Read-only usage client"
                                }
                            });
                        continue;
                    }
                    var id = m.Num("id");
                    if (id == 4 && quota is not null)
                    {
                        AccountActivity? activity = null;
                        try
                        {
                            if (m.Get("result") is { } r)
                                activity = r.Deserialize<AccountActivity>(Json.Options)?.Recent(quota.FetchedAt);
                        }
                        catch (JsonException) { }
                        return quota with
                        {
                            Activity = activity
                        };
                    }
                    if (id is not (1 or 2 or 3))
                        continue;
                    if (m.Get("error") is not null)
                        throw PaceException.Network();
                    if (m.Get("result") is not { ValueKind: JsonValueKind.Object } result)
                        throw PaceException.Invalid();
                    switch (id)
                    {
                        case 1:
                            await Send(new
                            {
                                method = "initialized",
                                @params = new
                                {
                                }
                            });
                            await Send(new
                            {
                                id = 2,
                                method = "account/read",
                                @params = new
                                {
                                    refreshToken = false
                                }
                            });
                            break;
                        case 2:
                            if (result.Get("account") is not { } acc || acc.Str("type") != "chatgpt" || (acc.Str("id") ?? acc.Str("email")) is not { } identity)
                                throw PaceException.Login();
                            account = UsageParser.Hash(JsonSerializer.Serialize(new
                            {
                                identity,
                                workspace = result.Get("workspaceRouting")
                            }));
                            await Send(new
                            {
                                id = 3,
                                method = "account/rateLimits/read"
                            });
                            break;
                        case 3:
                            if (account is null)
                                throw PaceException.Login();
                            ResetCredits? credits = null;
                            if (result.Get("rateLimitResetCredits") is { ValueKind: JsonValueKind.Object } c)
                                try
                                {
                                    credits = c.Deserialize<ResetCredits>(Json.Options);
                                }
                                catch (JsonException) { }
                            quota = new(account, DateTimeOffset.UtcNow, UsageParser.Codex(result), credits);
                            historyDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                            await Send(new
                            {
                                id = 4,
                                method = "account/usage/read"
                            });
                            break;
                    }
                }
            }
            if (quota is not null)
                return quota;
            throw PaceException.Network();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (quota is not null) return quota; throw new PaceException("Codex 同步超时，请检查登录和网络。", "Codex sync timed out. Check sign-in and connection."); }
        finally
        {
            lifetime.Cancel();
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    static async Task Drain(StreamReader reader, CancellationToken ct)
    {
        try
        {
            var b = new char[4096];
            while (await reader.ReadAsync(b.AsMemory(), ct) > 0)
            {
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
    static async Task<string?> ReadBoundedLine(StreamReader reader, CancellationToken ct)
    {
        // Character buffering stays inside StreamReader; enforce an explicit response cap.
        var result = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), ct) > 0)
        {
            if (one[0] == '\n')
                return result.ToString();
            if (result.Length >= 4_000_000)
                throw PaceException.Invalid();
            result.Append(one[0]);
        }
        return result.Length == 0 ? null : result.ToString();
    }
}
