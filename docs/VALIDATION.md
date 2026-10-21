# Validation and release boundary

## Automated

- Daily budgets: fixed baseline, overspending, midnight, timezone, fractional days, missing/expired fields, jitter and resets.
- Claude hourly allowance: rolling time, final partial hour, expired and unknown resets, countdown boundaries.
- Independent account/provider history and persistence.
- Codex JSON-RPC subprocess integration, optional history, unsolicited request rejection and bounded timeout.
- Claude exact origin, organization membership, Fable zero usage and normalized bucket separation, auth/429 handling.
- Monthly history filtering/deduplication, sparse patterns, reset advice and expired credits.
- Public reset source links, freshness and date precision.
- Windows compilation, self-contained publication, WPF rendering in English/Chinese using synthetic accounts, and three programmatic tray-panel open/hide cycles.

## Required real-device checks before calling this stable

- Codex CLI installed through npm, desktop bundled executable selection and an explicit WSL distribution.
- Real Claude email/SSO login, verification, multiple organizations, session reuse after restart, throttling, expiry, disconnect and in-flight cancellation.
- Explorer restart, hidden tray area, keyboard-only navigation, screen reader, light/dark/high-contrast themes, multi-monitor 100/125/150/200% scaling and Windows ARM64.
- Suspend/resume, network transitions, midnight and timezone transitions while the app is open.
- Per-user installer upgrade/uninstall and optional startup registration, missing WebView2 runtime.

CI rendering is not an interactive sign-in or physical-device test. Initial artifacts are unsigned beta builds. Signing/notarization of the macOS product is unrelated to Windows Authenticode signing.
