# CodexReset Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver a Windows CodexReset with functional parity to the reference macOS project.

**Architecture:** A testable .NET 8 core owns Codex discovery, local state, JSON-RPC and recovery orchestration. Thin CLI and Windows tray frontends consume the core; protocol-first continuation is isolated from Windows UI Automation fallback.

**Tech Stack:** C# 12, .NET 8, xUnit, System.Text.Json, Microsoft.Data.Sqlite, Windows tray UI.

**Spec:** docs/superpowers/specs/2026-10-04-codex-reset-windows-design.md

## Global Constraints
- Windows 10/11 x64.
- Functional parity with boyso/codex-reset is the acceptance target.
- Read Codex local state read-only and store no OpenAI credentials.
- Prefer local app-server thread/resume + turn/start; UI Automation is fallback only.
- Newly discovered conversations are never automatically selected.

## Review Focus
- Missing/moved CODEX_HOME must fail clearly.
- Protocol/schema changes must be detected and reported.
- Multiple selected threads resume once per recovery transition.
- GUI fallback must never type into unrelated windows.
- Restart during a limited window must avoid uncontrolled duplicate sends.

### Task 1: Solution skeleton and protocol models
Create solution/projects, JSON-RPC models, Codex home/config discovery, tests and Windows CI.

### Task 2: Local conversation and usage-limit discovery
Implement read-only local history discovery for all and usageLimitExceeded conversations, filtering/grouping metadata and tests.

### Task 3: App-server transport and capability discovery
Implement Windows desktop remote_control discovery plus private app-server fallback where supported. Add account/rateLimits/read, thread/resume, turn/start, thread/turns/list and unsubscribe.

### Task 4: Recovery and auto-continue engine
Implement 30-second polling, limited -> recovered transition, selection persistence, duplicate suppression, reset history and completion monitoring.

### Task 5: Diagnostic CLI
Implement query/status, threads, continue <thread-id>, and watch. Read-only commands never start turns.

### Task 6: Windows tray application
Implement 5h/1w usage, countdown, plan/credits, paused/all conversations, selection, command, auto-resume, manual continue, deep-link, history and connection status.

### Task 7: Windows GUI fallback and startup
Implement guarded UI Automation fallback and start-at-sign-in.

### Task 8: Parity verification and release
Verify against source behavior, run tests/build, add self-contained Windows x64 packaging and documentation. Do not claim protocol parity until Windows diagnostics confirm installed Codex behavior.
