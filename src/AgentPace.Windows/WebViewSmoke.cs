using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace AgentPace.Windows;
// Only invoked by --smoke-test. A disposable profile and intercepted local responses
// exercise the same CDP bridge without contacting Claude or using a real account.
internal static class WebViewSmoke
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentPace-WebViewTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><title>Agent Pace test fixture</title><p>Synthetic quota fixture</p>");
        using var view = new WebView2();
        var host = new Window { Width = 500, Height = 320, ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Content = view };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            host.Show();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            const string org = "00000000-0000-0000-0000-000000000001";
            var requests = new List<string>();
            var usageFixture = """{"five_hour":{"utilization":40,"resets_at":"2026-10-21T15:00:00Z"},"limits":[{"kind":"weekly_scoped","percent":0,"resets_at":"2026-10-28T12:00:00Z","scope":{"model":{"display_name":"Fable"}}}],"secret":"must not cross the bridge"}""";
            core.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                requests.Add(e.Request.Method + " " + uri.AbsolutePath);
                string json;
                if (uri.Host != "claude.ai")
                    json = "{}";
                else if (uri.AbsolutePath == "/api/organizations")
                    json = JsonSerializer.Serialize(new[] { new { uuid = org, name = "Synthetic account" } });
                else if (uri.AbsolutePath.EndsWith("/usage"))
                    json = usageFixture;
                else if (uri.AbsolutePath == "/index.html")
                {
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><title>Synthetic quota fixture</title>")), 200, "Fixture", "Content-Type: text/html");
                    return;
                }
                else
                    json = "{}";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(json)), uri.Host == "claude.ai" ? 200 : 403, "Fixture", "Content-Type: application/json");
            };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, _) => ready.TrySetResult();
            core.Navigate("https://claude.ai/index.html");
            await ready.Task.WaitAsync(timeout.Token);
            var reply = await ClaudeSession.EvaluateBridge(core, "", timeout.Token);
            if (reply.Contains("must not cross"))
                throw new InvalidOperationException("Bridge leaked unrelated data");
            using var data = JsonDocument.Parse(reply);
            var snapshot = UsageParser.Claude(data.RootElement, DateTimeOffset.UtcNow);
            if (snapshot.Windows.Count != 2 || snapshot.Windows.Single(w => w.BucketId == "claude_fable").Window.Remaining != 100)
                throw new InvalidOperationException("WebView quota normalization failed");
            usageFixture = """{"five_hour":{"utilization":2,"resets_at":"2026-11-01T18:00:00Z"},"limits":[{"kind":"session","percent":2,"is_active":false},{"kind":"weekly_all","percent":3,"is_active":true},{"kind":"weekly_scoped","percent":2,"is_active":false,"scope":{"model":{"display_name":"Fable"}}}]}""";
            using var inactive = JsonDocument.Parse(await ClaudeSession.EvaluateBridge(core, "", timeout.Token));
            var current = UsageParser.Claude(inactive.RootElement, DateTimeOffset.UtcNow);
            if (current.Windows.Count != 1 || current.Windows[0].Id != "claude/primary" || current.Windows[0].Window.UsedPercent != 3)
                throw new InvalidOperationException("Inactive modern limits revived legacy windows");
            usageFixture = """{"limits":[{"kind":"session","is_active":false},{"kind":"weekly_all","is_active":false}]}""";
            using var allInactive = JsonDocument.Parse(await ClaudeSession.EvaluateBridge(core, "", timeout.Token));
            if (UsageParser.Claude(allInactive.RootElement, DateTimeOffset.UtcNow).Windows.Count != 0)
                throw new InvalidOperationException("Confirmed inactive limits did not clear windows");
            if (!requests.Contains("GET /api/organizations") || !requests.Contains("GET /api/organizations/"+org+"/usage") || requests.Any(r => !r.StartsWith("GET ")))
                throw new InvalidOperationException("Unexpected write request");
            var mismatch = await ClaudeSession.EvaluateBridge(core, "00000000-0000-0000-0000-000000000002", timeout.Token);
            using var invalid = JsonDocument.Parse(mismatch);
            if (invalid.RootElement.Num("status") != 409)
                throw new InvalidOperationException("Saved organization mismatch was ignored");
        }
        finally { host.Close(); }
    }
}
