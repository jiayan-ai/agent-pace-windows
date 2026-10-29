# Agent Pace for Windows

English · [简体中文](README.zh-CN.md) · [macOS version](https://github.com/jiayan-ai/agent-pace-mac)

A native Windows tray app for pacing **Codex and Claude subscription usage**, built with **C#, WPF and WebView2**. Know what you can use today, your suggested hourly allowance in Claude's five-hour window, and when the next reset happens.

[Downloads](https://github.com/jiayan-ai/agent-pace-windows/releases) · [Build checks](https://github.com/jiayan-ai/agent-pace-windows/actions)

## Status

Initial Windows beta. Core and JSON-RPC tests, a Windows build, and synthetic WPF UI smoke checks are automated in GitHub Actions. These checks do **not** validate real Codex/Claude account sign-in, physical multi-monitor DPI changes, sleep/wake, or every identity provider. See [validation](docs/VALIDATION.md).

Downloads are **unsigned**. Windows may show SmartScreen warnings. No signing certificate or Microsoft Store approval is claimed. Verify the source and SHA-256 checksum before running; do not disable system security protections.

## Install

Targets Windows 11 x64. Download the `-setup.exe` installer or the portable ZIP from Releases. The installer is per-user and does not request administrator rights. The ZIP must be fully extracted; run `AgentPace.exe` in its extracted folder. Closing the details window keeps the app in the tray; use **Quit** to exit.

The release includes the .NET desktop runtime. **Microsoft Edge WebView2 Runtime** is needed for Claude sign-in and the community forecast window. If missing, install it from [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). Portable builds keep data in the user profile, not next to the executable.

The tray uses a quota progress icon and a hover summary. Click it for details. Windows does not provide the macOS-style long text beside standard tray icons; **Pin window** keeps the details visible. You can move the icon out of Windows' hidden-icons area in taskbar settings.

## Features

- Codex / Claude provider switch with independent account and window history.
- Fixed daily budgets for weekly windows, negative overspending, and a red details balance.
- Claude five-hour rolling hourly allowance with a highlighted reset countdown. The final partial hour shows actual remaining quota; expiration waits for a fresh server response.
- Fable, Sonnet and Opus weekly windows when the account reports them. Fable is identified by an explicit model-scoped weekly limit, not an internal codename.
- Codex reset credit counts, expiry details and local rule-based advice using 30 days of account activity.
- Use a specific Codex reset credit directly in the app, with confirmation before it is spent.
- Tibo public reset records, verified source links and a separate community forecast browser.
- Immediate English / Simplified Chinese switch; saved language and quota choices.
- Five-minute quota sync, wake refresh, fifteen-second countdown updates, optional launch at Windows sign-in, single-instance activation and a tray context menu.
- An explicit executable override and optional WSL distribution for Codex.

## Connect Codex

Sign in to a subscription account with a Windows Codex CLI installation. Agent Pace searches PATH and standard npm vendor locations for `codex.exe`. If not found, expand **Settings & calculation**, select the executable and save. A CLI installed only inside WSL is a different environment: enter its distribution name to run `codex app-server` there. `codex` must be executable through that distribution's normal PATH, without requiring an interactive shell.

Auto-discovery of every Microsoft Store / desktop-app package layout is not guaranteed. Choose the bundled `codex.exe` explicitly when needed. This app never reads `auth.json`, passwords or authentication tokens. API-key-only accounts do not provide subscription quota.

The client launches a local subprocess and uses `initialize`, `account/read`, `account/rateLimits/read` and optional `account/usage/read`. When you confirm manual redemption or enable automatic redemption, it also calls `account/rateLimitResetCredit/consume`, a write operation that can spend a reset credit. It rejects unsolicited server requests and never starts model work. Missing history in older servers does not prevent quota sync.

## Connect Claude

Choose **Claude Code → Connect / verify Claude**. Sign in to the same account used by Claude Desktop Code at `claude.ai` in the app-owned window. Complete any site verification yourself and click **Sync usage**. If several organizations exist, choose one explicitly.

The WebView2 session persists across launches. Agent Pace does not read Claude Desktop storage, Windows Credential Manager or another browser's cookies. An expired session can still require signing in again. This is ordinary Claude web authentication, not an official quota-only OAuth grant. Site changes and embedded-browser restrictions may require maintenance.

The quota script runs in a separate JavaScript execution world on the exact `https://claude.ai` origin. Only organization and allowlisted quota fields cross into the app. It uses same-origin GET requests; no cookie export, page-message bridge or native host object is exposed. **Disconnect Claude** stops syncing, clears this app's browsing data and removes its cached Claude quota history.

Claude's apps share subscription limits. The app cannot isolate usage caused only by Desktop Code. Fable usage also counts toward the overall weekly quota and is constrained by the five-hour limit.

## Pacing

At the first successful sync each local day:

```text
remaining = clamp(100 - usedPercent, 0, 100)
days = max(1, secondsUntilReset / 86400)
dailyAllowance = remaining / days
availableToday = min(currentRemaining, dailyAllowance - observedUseSinceFirstSync)
```

The daily allowance stays fixed until the next day, account/timezone change, a reset, or a decrease in reported usage. Small reset timestamp jitter preserves the baseline. Usage before first sync cannot be reconstructed. Overspending stays negative and is not deducted twice on the following day.

When an available Codex reset credit expires within 72 hours and before the selected main Codex window's natural reset, the expiry becomes the planning deadline. The panel and tray show a rolling hourly suggestion instead of the daily balance: 50% remaining and a credit expiring in 10 hours gives **5% / hour**. During the final partial hour the app shows the actual remaining quota before expiry. When no credit meets these conditions, the normal daily budget remains in view. The calculation uses the earliest eligible credit and confirmed account data; unknown expiry times are never guessed.

Credit expiry itself does **not** automatically reset quota. The natural reset remains separate. If the snapshot still lists a credit as available after its recorded expiry, refresh to confirm its status.

Claude five-hour pacing uses `remaining / max(1, hoursUntilReset)`. With 60% left and three hours remaining, the allowance is **20% / hour**. With less than one hour left, the app shows the actual quota available before reset. These are percentage points, **not minutes of work**; other limits still apply.

Missing data is never treated as zero. Cached/unconfirmed values are labeled and use `≈` in the hover summary. Expired windows wait for server confirmation instead of assuming restored quota.

### Use a reset credit in the app

Select an available credit in the Codex reset section and confirm to use that specific credit. You do not need to enable automatic redemption or wait until the credit is near expiry. Agent Pace rechecks the current account and credit before sending the request; the server decides whether it can be used. A successful reset restores eligible limits and refreshes the displayed quota and credit count. Unused quota is not added to the restored allowance, and the next reset times can change.

If a network interruption leaves the result unknown, Agent Pace pauses redemption of other credits until the outcome is resolved. Any retry of that request keeps the same request key to avoid spending a second credit. Neither clicking refresh nor reopening the app creates a new redemption request for an uncertain attempt.

### Optional automatic reset redemption

Automatic redemption is **off by default**. If enabled, Agent Pace attempts to redeem the specific available Codex reset credit five minutes before it expires. The app must be running, the PC awake and an internet connection available; a sleeping or closed app cannot guarantee an on-time attempt. The server decides eligibility and may reject a request, so redemption is not guaranteed. A reset restores eligible usage limits rather than adding unused quota to the new allowance, and can change their next reset times. Public reset announcements and forecasts do not trigger redemption. You can turn the feature off at any time.

## Reset advice and public updates

Monthly advice uses complete UTC date labels from the preceding 30 days, deduplicated without summing duplicate buckets. Missing days remain unknown. Patterns require at least 14 valid days and fresh history; weekday estimates need at least three observations and are smoothed toward the overall average. Token activity is not quota percentage and does not produce a precise depletion forecast.

Account reset credits are separate from public announcements. The anonymous [Codex Reset feed](https://codexreset.dev/) refreshes every 15 minutes; stale or failed source checks remain unconfirmed. Date-only records do not imply precise local delivery. Only validated source links are opened. Public messages never increase budgets or automatically redeem credits.

[Will Codex Reset](https://willcodexreset.com/) opens only on request in a separate private browser context. Probabilities are displayed by the website, not scraped, cached or treated as OpenAI commitments. Claude does not currently supply monthly account history or reset credit inventory through this integration; neither is invented.

## Privacy

Local settings, account hashes, quota snapshots, budget baselines and history are stored under `%LOCALAPPDATA%\AgentPace`. Claude authentication remains in the dedicated WebView2 profile in that directory. It is not a token-only authorization. Public feed caching is separate. No analytics, model inference or remote usage-history upload is included. Errors do not log raw account responses.

The app uses normal user permissions. Optional startup registration writes only the current user's Run entry. Uninstalling preserves local data and sign-in state; use Disconnect Claude before uninstalling if you want that session cleared. Do not share the data directory in bug reports.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). On Windows:

```powershell
dotnet run --project tests/AgentPace.Checks -c Release
dotnet build src/AgentPace.Windows -c Release
pwsh scripts/package.ps1
```

The core tests can run on macOS/Linux. WPF execution requires Windows. `--demo` uses synthetic data in a fresh temporary directory. `--smoke-test` renders both languages and exercises window opening with synthetic data, then exits with a test status. Never use private account state in CI.

The Windows workflow produces a self-contained x64 ZIP, per-user installer and SHA-256 checksums. Inno Setup 6 is required to build the installer. Code signing is intentionally not claimed or simulated. See [architecture](docs/ARCHITECTURE.md) and [validation](docs/VALIDATION.md).

Not affiliated with OpenAI or Anthropic. This repository does not currently specify an open-source license.
