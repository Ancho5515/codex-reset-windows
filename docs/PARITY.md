# Parity checklist

Reference: boyso/codex-reset

- [x] Resolve CODEX_HOME and local Codex databases
- [x] Detect usageLimitExceeded conversations
- [x] Browse non-archived, non-subagent conversations
- [x] Preserve meaningful original title after automatic Continue turns
- [x] Query account/rateLimits/read
- [x] Detect limited -> recovered transition
- [x] Explicit selected-conversation persistence
- [x] thread/resume + turn/start continuation
- [x] turn completion polling + thread/unsubscribe
- [x] CLI doctor/status/threads/paused/continue
- [x] Windows tray app
- [x] 30-second polling
- [x] 5-hour usage/reset display
- [x] Deep-link to Codex thread
- [x] Windows sign-in registration primitive
- [ ] Confirm actual Windows Codex app-server invocation against an installed current Codex build
- [x] Confirm CI build/test artifact on GitHub Actions
- [ ] Secondary (weekly) usage visualization in tray
- [ ] Reset-history UI
- [ ] Credits/plan display
- [ ] Fully verified UI Automation text-entry fallback

The unchecked protocol/CI items are release gates; they must not be represented as verified until exercised.
