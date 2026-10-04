# CodexReset for Windows

Windows companion for Codex Desktop, ported from boyso/codex-reset.

The goal is functional parity: watch Codex usage limits, find conversations paused by `usageLimitExceeded`, and automatically continue selected conversations when usage becomes available again.

## Current commands

```powershell
dotnet run --project src/CodexReset.Cli -- doctor
dotnet run --project src/CodexReset.Cli -- status
dotnet run --project src/CodexReset.Cli -- threads
dotnet run --project src/CodexReset.Cli -- paused
dotnet run --project src/CodexReset.Cli -- continue <thread-id> "继续"
```

`doctor`, `threads` and `paused` are read-only. `status` starts a private local Codex app-server for querying rate limits. `continue` explicitly resumes a thread and starts a new turn.

No OpenAI credentials are stored by CodexReset. It uses the existing local Codex installation and `CODEX_HOME`.

## Windows protocol verification

Run `doctor` first, then `status`. Windows Codex builds can change internal app-server behavior; failures should be reported with the exact CLI output.

## Build

```powershell
dotnet restore CodexReset.sln
dotnet test CodexReset.sln -c Release
dotnet publish src/CodexReset.Cli -c Release -r win-x64 --self-contained true
```
