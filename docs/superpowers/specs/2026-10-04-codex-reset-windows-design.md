# CodexReset Windows Design

## Goal
Build a Windows companion for Codex Desktop with functional parity with boyso/codex-reset: monitor 5-hour/weekly usage, identify usage-limited conversations, and automatically resume selected conversations with a configurable command when usage recovers.

## Architecture
- .NET 8 / C#.
- CodexReset.Core: Codex home discovery, local state/history reading, app-server JSON-RPC transport, rate-limit/recovery state machine, selected-thread persistence and auto-continue orchestration.
- CodexReset.Cli: status/query/continue/watch commands for protocol diagnostics and headless operation.
- CodexReset.App: Windows tray application for usage, countdown, conversation selection, settings and reset history.
- Prefer Codex Desktop remote_control/local app-server. Fall back to a private local app-server where supported. Windows UI Automation/deep-link is fallback only.
- Store no OpenAI credentials. Read Codex state read-only.

## Functional parity
1. Live 5h / 1w usage and reset countdown.
2. Detect conversations stopped by usageLimitExceeded.
3. Browse conversations grouped by project.
4. Explicit per-conversation selection; newly discovered threads are not auto-selected.
5. On limited -> recovered transition, resume selected conversations once with configurable command.
6. Manual continue and codex://threads/<id> open.
7. CLI query/status, threads, continue <thread-id>, watch.
8. Persist reset history/settings locally.
9. Start at Windows sign-in.
10. Protocol-first continuation: thread/resume then turn/start; monitor completion and release subscription/write ownership.
11. UI Automation fallback must verify focus and never silently send to another application.

## Compatibility
The source project relies on reverse-engineered local Codex behavior. Protocol schemas and Windows endpoint discovery are isolated so Codex updates do not require UI/orchestration rewrites. Missing Windows capabilities produce explicit diagnostics rather than guessed behavior.

## Verification
- Unit tests for transitions, duplicate suppression, persistence and protocol request construction.
- Windows diagnostics are read-only unless continue is explicitly invoked.
- GitHub Actions builds/tests on windows-latest.
- Self-contained Windows x64 release after protocol verification.
