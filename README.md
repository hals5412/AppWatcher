# AppWatcher

[日本語 README](README.ja.md) | English

AppWatcher is a lightweight Windows supervisor for long-running desktop applications that should normally stay running.

> Status: **development alpha**. Core monitoring, Japanese/English UI, coordinated shutdown/restart, duplicate-registration prevention, and normal/elevated host IPC are implemented. Continue runtime testing before replacing an existing watchdog in production.

## Design goals

- Detect process exits and restart applications automatically.
- Optionally detect a hung GUI window and recover it.
- Keep normal-user and administrator applications in one dashboard.
- Make monitoring easy to pause during maintenance.
- Record *what happened, what AppWatcher decided, and why*.
- Keep the always-running components small and event-driven.
- Avoid changing the execution environment of monitored applications.
- Never hide, sandbox, or move monitored GUI applications to Session 0.
- Do **not** place monitored applications in a Windows Job Object by default.
- Do **not** terminate child processes by default.

The last three points are deliberate compatibility requirements for desktop applications that may launch other GUI child processes. A program started by AppWatcher should behave as closely as practical to one launched normally from the logged-on Windows desktop.

## Components

| Component | Purpose |
| --- | --- |
| `AppWatcher.Agent.exe` | Normal-user monitoring host and tray icon. |
| `AppWatcher.Elevated.exe` | Administrator monitoring host. No visible window. |
| `AppWatcher.UI.exe` | Dashboard, configuration editor, event log, diagnostics. Only runs when needed. |
| `AppWatcher.Core.dll` | Shared monitoring, configuration, logging and IPC logic. |

The Agent and Elevated helper run in the **interactive logged-on user session**. They are not Windows services.

## Implemented in this alpha

- Full-path process matching and attachment to already-running applications.
- Event-driven process exit detection (`Process.Exited`) rather than process-list polling.
- Interactive process launch with normal windows; no hidden desktop / Session 0 / Job Object.
- Restart on unexpected exit.
- Configurable restart delay.
- GUI-hang checking with a startup grace period and timeout.
- Restart-loop protection and backoff.
- Per-application timed or indefinite pause.
- Global maintenance mode.
- Graceful close followed by optional force termination of the **parent only**.
- Normal and elevated monitoring hosts with separate named-pipe endpoints.
- WinForms dashboard with a combined normal/admin application list.
- Host PID, uptime and working-set display.
- JSON configuration with three rotating backups.
- SQLite event history in WAL mode.
- Detailed reason codes for automatic decisions.
- Event log viewer.
- Diagnostic ZIP creation with obvious secret-like command-line arguments masked.
- Task Scheduler setup for normal and highest-privilege startup at user logon.
- Automatic startup self-check for missing, disabled, or stale Task Scheduler registration.
- Running-application picker with normal/elevated privilege detection.
- Application-scoped event-log views keyed by stable application ID.
- Versioned configuration schema migration with safe downgrade rejection.
- Japanese and English UI with automatic Windows display-language selection.


## Language

AppWatcher supports **Japanese** and **English**. The default is `Auto`, which follows the Windows display language (Japanese Windows uses Japanese; other languages currently fall back to English).

The language can be changed from **Tools → Settings...**. The setting is stored as `global.uiLanguage` in `config.json` with one of these values:

```text
Auto
Japanese
English
```

Internal state names, event codes, and reason codes remain language-neutral so logs and diagnostics stay machine-readable. The event log shows localized descriptions while preserving the original codes in the details pane.

## Important compatibility behavior

For a normal application AppWatcher uses the logged-on user's interactive token and normal desktop. The launch code intentionally does **not** use `CREATE_NO_WINDOW`, `DETACHED_PROCESS`, a hidden desktop, a service, or a Job Object.

The default child-process policy is **Unmanaged**. If a monitored application launches another GUI process, AppWatcher does not take ownership of that child process by default. The child application should therefore be able to display and continue running normally on the logged-on desktop. This behavior is a mandatory manual test before the first stable release.

## Requirements

Development:

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022+ with .NET desktop development workload, or the `dotnet` CLI

Framework-dependent published builds require the .NET 10 Desktop Runtime. The publish script can also make a self-contained build.

## Build

```powershell
dotnet restore .\AppWatcher.sln
dotnet build .\AppWatcher.sln -c Release
```

To create a combined runnable folder:

```powershell
.\scripts\publish.ps1
```

For a self-contained win-x64 package:

```powershell
.\scripts\publish.ps1 -SelfContained -PackageSuffix "-self-contained"
```

## Download and update

Open [GitHub Releases](https://github.com/hals5412/AppWatcher/releases) and choose the desired alpha prerelease. Both packages are for Windows x64:

| File | Requirements |
| --- | --- |
| `AppWatcher-win-x64.zip` | .NET 10 Desktop Runtime (x64) installed separately. |
| `AppWatcher-win-x64-self-contained.zip` | Includes the .NET runtime; larger download. |
| `SHA256SUMS.txt` | SHA-256 checksums for both ZIPs. |

Extract the entire ZIP into one folder and start `AppWatcher.UI.exe` as a normal user. To update, first use **Exit AppWatcher completely**, then replace the complete application file set so Agent, Elevated, UI, and Core come from the same version. Monitored applications are left running. Settings and logs remain in the separate data directory below. If the installation folder changes, install/repair the startup tasks again.

## First run

1. Extract a release ZIP or publish AppWatcher so `AppWatcher.Agent.exe`, `AppWatcher.Elevated.exe`, and `AppWatcher.UI.exe` are in the same folder.
2. Start `AppWatcher.UI.exe`.
3. Open **Tools → Install / repair startup tasks...**.
4. Approve the one-time UAC prompt.
5. Add monitored applications from the dashboard.

The startup installer creates two Task Scheduler tasks under `\AppWatcher`:

- `Agent`: interactive normal-user token.
- `Elevated`: interactive token with highest privileges.

This avoids a UAC prompt every time an administrator application has to be restarted, while still keeping it in the logged-on desktop session.

## Data files

Stored under:

```text
%LOCALAPPDATA%\AppWatcher\
```

Main files:

```text
config.json
config.backup-1.json
config.backup-2.json
config.backup-3.json
events.db
events.db-wal
events.db-shm
appwatcher-fallback.log
```

## Safety / operational behavior

Stopping AppWatcher itself **does not terminate monitored applications**. Removing an application from configuration also leaves the target process running.

`Stop` from the dashboard suppresses automatic restart until an explicit Start or Restart during the same host session. Resume and configuration reloads do not clear this intent, even if stopping the target failed.

On Windows session-ending notification, AppWatcher marks shutdown in progress and suppresses new automatic launches.

## Known alpha limitations

- Timed pause / maintenance state currently lives in host memory; persistence across an unexpected AppWatcher host restart is planned.
- `ChildProcessPolicy.TrackOnly` and `StopWithParent` are reserved for future versions; v0.1 only uses `Unmanaged` in the UI.
- TCP/HTTP health checks are not yet implemented.
- Per-target CPU/RAM history is not collected. This is intentional until the overhead model is measured.
- Update checking is intentionally disabled by default and is not implemented in this alpha.
- Language changes require the Agent/dashboard to be restarted before every component uses the new language.

See [`docs/specification.md`](docs/specification.md), [`docs/architecture.md`](docs/architecture.md), and [`docs/manual-test-plan.md`](docs/manual-test-plan.md).


## Stopping or restarting AppWatcher

Closing the dashboard does **not** stop monitoring. Use **Exit AppWatcher completely** from the tray or dashboard when replacing binaries. AppWatcher first asks any open dashboard to close, suppresses automatic restart decisions, asks the elevated helper to terminate itself over IPC, then exits the normal Agent. Monitored applications are deliberately left running.

The same operation is available for scripted development workflows:

```powershell
AppWatcher.UI.exe --shutdown
AppWatcher.UI.exe --restart
```

Published packages also include `stop-appwatcher.ps1` and `restart-appwatcher.ps1`. `--restart` prefers the registered Task Scheduler tasks so the Elevated helper can return without a new UAC prompt. If those tasks are not installed, AppWatcher falls back to direct launch and Windows may show UAC for the Elevated helper.

## Reliability and logging

- While a host remains running, configuration reloads, Resume, and maintenance expiry preserve a manual Stop. Use Start or Restart explicitly to start that target again.
- Configuration reloads apply changes by application ID, preserving tracked processes, pause deadlines, restart history, and backoff. Explicitly stop a target before changing its executable or privilege level; a privilege transfer also preserves its stopped state.
- Pause and manual Stop are not persisted across host restarts. Startup settings are evaluated again when AppWatcher restarts.
- Global Pause/Resume from either the tray or dashboard addresses both hosts and reports partial failures. An offline, unused Elevated helper is not started just for this operation.
- Configuration uses a cross-process file lock and partial updates against the latest saved values. A lock wait exceeding 10 seconds fails the operation; retry manually.
- Log database failures do not stop monitoring. Logging uses a queue of up to 1,024 events and reports overflow counts to the fallback log. Failed database attempts are retried no more than once per minute.
- Old events are cleaned up after startup and hourly. `global.eventDatabaseMaxMegabytes` is a cleanup target, defaulting to 100 MiB with a minimum of 10 MiB. When exceeded, older events are deleted and the database is compacted when possible. This is not a strict instantaneous limit: WAL growth and lock contention can cause temporary excess usage or defer compaction.
- The fallback log rotates at 5 MiB and retains the current file plus two older generations.
- Diagnostic ZIPs mask supported command-line argument forms named `password`, `passwd`, `token`, `api-key`, `api_key`, and `secret`, including whitespace-separated, equals-separated, and quoted values. Historical database records are masked into a new diagnostic database; the original database is not changed. Arbitrary secrets are not guaranteed to be removed.
- Automated tests are separate projects. Test runners and test helper processes are excluded from distribution ZIPs.

See the [reliability improvement plan](docs/AppWatcher-reliability-improvement-plan.md) and [implementation and validation report](docs/AppWatcher-reliability-implementation-report.md) (Japanese). Real UI and normal/admin interaction checks in an isolated Windows environment, and before/after resource measurements, remain outstanding.
