# Architecture

- **AgentPace.Core**: framework-independent models, daily and hourly pacing, provider history, language selection, JSON-RPC client, Claude quota bridge, reset analysis and bounded public feed parsing.
- **AgentPace.Windows**: WPF window, Windows Forms NotifyIcon, per-user startup registration, single-instance signaling, WebView2 login and a separate private forecast window.
- **AgentPace.Checks**: executable regression suite including a subprocess JSON-RPC fake. No private account data is used.

The original macOS source remains in `jiayan-ai/agent-pace-mac`. Windows ports the calculation semantics and the allowlisted Claude bridge. UI and operating-system services are native Windows implementations. The apps do not share a data directory or session credentials.

WebView2 executes the Claude bridge via CDP `Page.createIsolatedWorld` and `Runtime.evaluate` with `awaitPromise` and `returnByValue`. It checks the root frame origin before execution and the current source after completion. The selected organization is JSON-encoded as data. Native host objects and web messages are disabled; downloads and permission requests are denied. Visible HTTPS sign-in redirects and popups can use the user's identity provider without weakening browser TLS or verification.

Codex uses explicit subprocess arguments, bounded reads/timeouts and process-tree cleanup. Windows .exe and explicit WSL routes avoid running interpolated shell commands. Unknown server-initiated requests receive a method-not-found response. Only read-only account/usage calls are sent.

The public feed has no cookies or credentials, disallows redirects, caps response bytes and validates original-source URLs. Public events cannot modify account state. The forecast browser has a separate private context and no script/native bridge.

Language changes update the WPF tree and tray menu. UI updates pause while a combo menu is open or a settings text field has focus; closing the combo rerenders. Daily budget state is independent of UI language. Cached state uses atomic file replacement in the current user's LocalAppData directory.
