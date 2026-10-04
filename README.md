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


## Tray app

Publish and run `CodexReset.App.exe`. The tray menu shows the current 5-hour usage, reset time, and conversations. Check the conversations that should be automatically resumed. Newly discovered conversations are never selected automatically.

The app polls every 30 seconds. A selected conversation is continued only when the observed state changes from limited to available.

## Start at sign-in

The core includes per-user Windows Run-key registration. The packaged app will expose this setting in the tray UI.

## Important compatibility note

CodexReset uses Codex Desktop's local state and app-server protocol. These are not a stable public API. This Windows port deliberately keeps the protocol in an isolated core layer. Run `CodexReset.Cli.exe doctor` and `status` after a Codex update if automatic continuation stops working.
