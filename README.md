# CodexReset for Windows

A Windows port of [boyso/codex-reset](https://github.com/boyso/codex-reset), a companion for Codex Desktop that keeps selected conversations moving after a usage-limit pause.

CodexReset watches Codex's local usage state. When a selected conversation has stopped because of `usageLimitExceeded` and the account later becomes available again, it resumes that same thread and starts a new turn with a configurable continuation command (default: `继续`).

> This project is unofficial. It uses Codex local state and the local app-server protocol, which are not guaranteed to remain stable across Codex releases.

## Status

The Windows solution builds and tests successfully in GitHub Actions on `windows-latest`, including self-contained `win-x64` publishing for the tray app and CLI.

The remaining release gate is runtime verification against a current Codex installation on Windows. In particular, the local Codex app-server invocation and these RPC calls must be exercised on a real Windows machine:

```text
initialize
initialized
account/rateLimits/read
thread/resume
turn/start
thread/turns/list
thread/unsubscribe
```

Do not treat automatic continuation as production-verified until that local validation succeeds.

## What it does

- Resolves `CODEX_HOME` and reads the existing Codex local state.
- Reads the 5-hour usage window and reset time through the local app-server.
- Finds conversations whose latest failed turn contains `usageLimitExceeded`.
- Lists normal Codex conversations while filtering archived and sub-agent threads.
- Lets the user explicitly select which conversations may be automatically resumed.
- Detects the transition from usage-limited to available.
- Resumes selected threads with `thread/resume` and starts a new turn with `turn/start`.
- Waits for the new turn to finish and calls `thread/unsubscribe` to release ownership.
- Provides a Windows tray application with 30-second polling.
- Provides a diagnostic CLI for local protocol verification.
- Supports `codex://threads/<thread-id>` deep links.
- Contains a Windows per-user start-at-sign-in registration primitive.
- Never automatically selects newly discovered conversations.

## How continuation works

CodexReset does **not** revive the exact interrupted turn. The behavior matches the reference project conceptually:

```text
existing task
    |
    v
Codex turn stops with usageLimitExceeded
    |
    v
CodexReset observes the account as limited
    |
    v
usage becomes available again
    |
    v
thread/resume(existing thread id)
    |
    v
turn/start("继续")
    |
    v
Codex receives the existing conversation context and continues the work
    |
    v
wait until turn is no longer inProgress / queued / pending
    |
    v
thread/unsubscribe
```

Only conversations selected by the user are eligible for automatic continuation.

## Repository layout

```text
src/
  CodexReset.Core/       Codex state, SQLite, RPC, recovery and continuation logic
  CodexReset.Cli/        Windows diagnostics and manual continuation
  CodexReset.App/        Windows tray application
tests/
  CodexReset.Core.Tests/ Core behavior and protocol-shape tests
docs/
  PARITY.md              Feature/release parity checklist
  superpowers/specs/     Design specification
  superpowers/plans/     Implementation plan
.github/workflows/
  ci.yml                 Windows build, test and publish workflow
```

## Requirements

For development:

- Windows 10 or Windows 11 x64
- .NET 8 SDK
- A current Codex Desktop/CLI installation for runtime protocol validation

The GitHub Actions release artifact is self-contained, so a published executable should not require a separately installed .NET runtime. Codex itself is still required because CodexReset uses the existing local Codex installation and account state.

## Build and test

From the repository root:

```powershell
dotnet restore CodexReset.sln
dotnet test CodexReset.sln -c Release
dotnet build CodexReset.sln -c Release
```

Publish the tray application:

```powershell
dotnet publish src/CodexReset.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/app
```

Publish the CLI:

```powershell
dotnet publish src/CodexReset.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/cli
```

GitHub Actions performs the tests and both publish operations on `windows-latest` and uploads the resulting `codex-reset-windows-x64` artifact.

## Codex home

CodexReset resolves the Codex home in this order:

1. `CODEX_HOME`, when explicitly configured.
2. `%USERPROFILE%\.codex`.

Expected local data currently includes:

```text
%CODEX_HOME%\state_5.sqlite
%CODEX_HOME%\thread_history_1.sqlite
%CODEX_HOME%\config.toml
```

The database schema is an internal Codex implementation detail and can change.

## CLI

Run from source:

```powershell
dotnet run --project src/CodexReset.Cli -- doctor
dotnet run --project src/CodexReset.Cli -- status
dotnet run --project src/CodexReset.Cli -- threads
dotnet run --project src/CodexReset.Cli -- paused
dotnet run --project src/CodexReset.Cli -- continue <thread-id> "继续"
```

Or use the published `CodexReset.Cli.exe`.

### `doctor`

Read-only environment diagnostics. It reports:

- resolved `CODEX_HOME`
- whether `state_5.sqlite` exists
- whether `thread_history_1.sqlite` exists
- whether `remote_control` is enabled in `config.toml`
- detected Codex CLI path, when found

Run this first during Windows validation.

### `status`

Starts a private local Codex app-server, performs the Codex app-server handshake, and calls:

```text
account/rateLimits/read
```

It prints the returned JSON. This is the first important Windows runtime compatibility test.

### `threads`

Read-only. Lists normal, non-archived, non-subagent conversations from the local Codex state database.

### `paused`

Read-only. Lists conversations with a latest failed turn containing `usageLimitExceeded`, including the recovery hint when available.

### `continue`

**Writes to the selected Codex conversation.**

```powershell
CodexReset.Cli.exe continue <thread-id> "继续"
```

The command performs approximately:

```text
thread/resume
turn/start
thread/turns/list
thread/unsubscribe
```

Use a disposable/test conversation for the first Windows protocol validation.

## Tray application

Run `CodexReset.App.exe`.

The tray application:

- connects to the local Codex app-server
- polls every 30 seconds
- shows the current 5-hour usage percentage and reset time
- lists local conversations
- marks usage-limited conversations
- persists explicitly selected conversations
- detects a limited -> available transition
- automatically continues eligible selected conversations
- can open a conversation using the Codex deep link

Newly discovered conversations are **not** selected automatically.

## Local Windows validation procedure

After cloning this repository onto the Windows machine that runs Codex, use the following sequence.

### 1. Verify the repository itself

```powershell
git pull
dotnet test CodexReset.sln -c Release
```

Expected result: all tests pass.

### 2. Verify local Codex discovery

```powershell
dotnet run --project src/CodexReset.Cli -- doctor
```

Check that the resolved Codex home is the one used by your installed Codex and that the expected databases exist.

If the Codex home is elsewhere:

```powershell
$env:CODEX_HOME = "D:\path\to\codex-home"
dotnet run --project src/CodexReset.Cli -- doctor
```

### 3. Verify read-only SQLite discovery

```powershell
dotnet run --project src/CodexReset.Cli -- threads
dotnet run --project src/CodexReset.Cli -- paused
```

Confirm that thread IDs/titles correspond to conversations visible in Codex.

### 4. Verify the Windows app-server

```powershell
dotnet run --project src/CodexReset.Cli -- status
```

Success criteria:

- the Codex app-server process starts
- WebSocket connection succeeds
- `initialize` succeeds
- `initialized` is accepted
- `account/rateLimits/read` returns a rate-limit object

If this fails, capture the full exception/output. The likely compatibility points are Codex executable discovery, app-server command-line syntax, WebSocket transport, or RPC schema.

### 5. Verify continuation on a test conversation

Create or choose a conversation where sending `继续` is harmless. Copy its thread ID from `threads`, then run:

```powershell
dotnet run --project src/CodexReset.Cli -- continue <thread-id> "继续"
```

Success criteria:

- the same thread is resumed
- a new user turn containing `继续` appears
- Codex starts working from the existing context
- after the turn finishes, the thread remains usable in Codex Desktop

Do not perform the first write-path test on an important conversation.

### 6. Verify automatic recovery behavior

After the protocol path is confirmed:

1. Run the tray app.
2. Select a disposable conversation.
3. Keep Auto continue enabled.
4. Observe a genuine usage-limit event.
5. Leave CodexReset running across the reset.
6. Confirm that the selected thread receives exactly one continuation turn after the account changes from limited to available.

Also confirm that unselected conversations are untouched.

## Instructions for Codex validating this repository locally

When using Codex itself to validate/fix this Windows port, give it this repository and ask it to work through the following release gate without stopping after the first error:

```text
Validate CodexReset for Windows against the Codex installation on this machine.

1. Read README.md, docs/PARITY.md, and the design/implementation docs.
2. Run the full Release test suite.
3. Run CodexReset.Cli doctor and verify CODEX_HOME/database/CLI discovery.
4. Run threads and paused and compare the results with the actual local Codex state.
5. Run status and diagnose/fix all Windows app-server discovery, process launch,
   WebSocket handshake, or JSON-RPC compatibility problems.
6. After the read-only path works, use a disposable conversation to validate
   continue <thread-id> "继续".
7. Verify thread/resume -> turn/start -> completion polling -> thread/unsubscribe.
8. Build and run the tray application and verify 30-second polling, conversation
   selection persistence, limited -> recovered detection, and duplicate suppression.
9. Run all tests again after every fix and add regression tests for compatibility bugs.
10. Update docs/PARITY.md only for behavior actually verified on this Windows machine.
11. Do not claim completion while any release-gate item remains unverified.
```

This is intentionally written so local Codex can use facts from the installed Windows environment instead of guessing platform-specific internals.

## Security and data handling

CodexReset is local-only by design:

- It does not ask for or store an OpenAI password.
- It does not maintain its own OpenAI authentication token.
- It uses the existing Codex installation and `CODEX_HOME`.
- Thread/database inspection is intended to be read-only.
- Only the explicit `continue` command or enabled automatic continuation starts a new turn.
- Newly discovered conversations are never automatically opted in.

The project interacts with internal local Codex state/protocols. Review the code before running it if that is a concern.

## Troubleshooting

### `doctor` cannot find the databases

Confirm which `CODEX_HOME` the installed Codex actually uses. Set `CODEX_HOME` explicitly before running CodexReset if necessary.

### Codex CLI is not found

Set `CODEX_CLI_PATH` to the actual Windows Codex executable/script path and rerun `doctor`/`status`.

### `status` cannot start app-server

Run the installed Codex executable manually with its app-server help/options and compare them with the invocation in `AppServerManager`. Current code expects an app-server that can listen on a local WebSocket endpoint.

### `initialize` or another RPC fails

Codex may have changed its internal app-server schema. Capture the complete request/response/error and update the isolated RPC adapter plus regression tests rather than adding UI-specific workarounds.

### Thread is resumed but Codex Desktop cannot reopen it

Verify that completion monitoring reaches a terminal state and that `thread/unsubscribe` succeeds. Writer ownership must be released after the continuation turn.

### Automatic continuation fires more than once

Treat this as a correctness bug. Check recovery-transition state and handled-thread persistence/duplicate suppression before using the app on important conversations.

## Compatibility philosophy

The reference macOS project relies on local Codex behavior rather than a stable public API. This Windows port therefore separates:

- local state discovery
- app-server transport
- RPC message construction
- recovery detection
- continuation orchestration
- Windows UI

When Codex changes, prefer fixing the narrow compatibility layer and adding a regression test.

## Parity

See [docs/PARITY.md](docs/PARITY.md) for the detailed parity/release checklist.

The GitHub Actions Windows build/test/publish gate is currently passing. Runtime Windows Codex protocol verification remains the main outstanding release gate.

## License

The upstream project is MIT licensed. Preserve applicable upstream attribution and license terms when redistributing derived work.
