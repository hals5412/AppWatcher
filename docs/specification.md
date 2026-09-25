# AppWatcher v1 specification

## 1. Product objective

AppWatcher is a low-overhead Windows desktop application supervisor. The primary objective is not merely to restart crashed programs, but to provide an auditable answer to:

1. What state is the application in now?
2. What happened previously?
3. Why did AppWatcher restart, not restart, pause, back off, or fail?
4. Did AppWatcher itself remain healthy?

Reliability and compatibility take precedence over feature count.

## 2. Design principles

### 2.1 Preserve normal application behavior

A monitored GUI program must run in the current logged-on user's interactive session and default desktop. Normal applications are started by the normal Agent; elevated applications are started by an elevated helper that is also in the same interactive session.

The default launch path must not use:

- Windows service / Session 0 execution
- hidden desktops
- `CREATE_NO_WINDOW`
- `DETACHED_PROCESS`
- forced window hiding
- Windows Job Objects

Child processes are unmanaged by default. This preserves compatibility with desktop applications that launch separate GUI child processes which must remain independent of the monitored parent.

### 2.2 Event-driven when possible

Process termination for an attached target is observed through process handles / `Process.Exited`. To attach applications launched externally after AppWatcher starts, `AttachExisting` targets without an attached process are checked every five seconds by configured executable name, followed by the existing full-path identity check. AppWatcher must not inspect every Windows process on each interval merely to discover a known process exit.

Periodic work is limited to features that inherently require it, such as GUI responsiveness checks.

### 2.3 Monitoring must fail independently

Failure of the GUI, SQLite log database, diagnostic system, or another monitored application must not stop supervision of otherwise healthy applications.

### 2.4 Every automatic action has a reason code

Examples:

- `UnexpectedProcessExit`
- `IntentionalStop`
- `MonitoringPaused`
- `RestartPolicyDeclined`
- `RestartLimitExceeded`
- `BackoffActive`
- `HangTimeoutExceeded`
- `WindowsShutdownInProgress`
- `ProcessStartFailed`

## 3. Runtime components

### AppWatcher.Agent

Normal-user background supervisor. Owns the tray icon and normal-integrity applications.

### AppWatcher.Elevated

Highest-privilege background supervisor. Has no normal UI and owns administrator applications.

### AppWatcher.UI

On-demand dashboard. Reads both hosts through named pipes and shows one combined list.

## 4. Application states

- `Unknown`
- `Starting`
- `Healthy`
- `Stopped`
- `Paused`
- `Unresponsive`
- `Restarting`
- `Backoff`
- `Failed`

## 5. Application configuration

Required or supported settings:

- Display name
- Executable full path
- Arguments
- Working directory
- Normal / Administrator privilege
- Monitoring enabled
- Start with watcher
- Attach to existing instance
- Restart policy
- Restart delay
- Hang detection enable
- Hang timeout
- Hang check interval
- Hang action (`LogOnly` or `Restart`; default `LogOnly`)
- Startup grace period
- Restart-loop protection enable
- Maximum restarts in time window
- Backoff duration
- Healthy-reset duration
- Graceful shutdown timeout
- Force terminate after graceful timeout
- Child process policy (v1 UI default and only supported mode: `Unmanaged`)
- Per-application log level

Default process identity match is full executable path, not just executable file name. An existing instance must also run in the current session, under the current user, and at the configured privilege level. A same-path instance at a different privilege level is treated as a different application.

## 6. Restart semantics

Default values:

- Restart delay: 5 s
- Startup grace: 15 s
- Hang interval: 10 s
- Hang timeout: 60 s
- Loop limit: 5 restarts / 10 min
- Backoff: 15 min
- Healthy reset: 30 min

Manual dashboard `Stop` is intentional and suppresses restart until an explicit Start or Restart, including across configuration reload and maintenance resume within the same host lifetime.

Automatic restart is evaluated after an unexpected exit according to the selected policy.

When `AttachExisting` is enabled, AppWatcher checks for a matching instance again immediately before an automatic or manual launch. An instance started during the restart delay (by the user, or by the target itself) is attached instead of launching a duplicate.

## 7. Hang detection

Only applications with hang detection enabled are periodically checked.

A window is not killed after one slow response. The state may become `Unresponsive`, and `HangDetected` is logged once the configured timeout has elapsed continuously.

With `HangAction = LogOnly` (default for new applications) the target keeps running and stays `Unresponsive` until its window responds again. With `HangAction = Restart` the parent process is terminated and the normal restart policy applies.

Child processes are not killed as part of hang recovery.

## 8. Maintenance controls

Per application:

- 15 min
- 1 h
- 4 h
- indefinite
- resume

Global maintenance mode offers equivalent controls.

A pause stops automatic supervision actions but does not terminate a currently running target.

## 9. Logging

SQLite is used for structured state/action history. WAL mode and normal synchronous mode are used to reduce contention and write overhead.

Events record:

- UTC timestamp
- application ID and name
- severity
- event type
- reason code
- JSON detail payload

The logging subsystem is non-fatal. If SQLite writing fails, a simple fallback text log is attempted and monitoring continues.

Normal operation does not write periodic "still healthy" records. Events are primarily state/action transitions.

## 10. Dashboard

Default columns:

- Application
- State
- Privilege
- PID
- Uptime
- Restarts in current protection window
- Last event
- Last reason
- Executable

Host status shows Agent / Elevated PID, uptime and working set.

## 11. Shutdown behavior

When Windows session ending is observed, hosts enter shutdown mode and do not schedule new automatic launches.

Closing AppWatcher hosts does not kill target processes.

## 12. Configuration storage

Configuration is JSON under `%LOCALAPPDATA%\AppWatcher`. Three generations of configuration backups are maintained. If the main file cannot be read, backups are tried in order.

The configuration format is versioned. Schema 1, schema 2 and pre-versioned files are migrated to the current schema (3) before use; applications that had hang detection enabled keep the previous force-restart behavior as `HangAction = Restart`, with the exact pre-migration file preserved as the newest backup. A configuration whose schema is newer than the running AppWatcher build supports is rejected instead of silently falling back to an older backup.

## 13. Diagnostics

The dashboard can generate a ZIP containing:

- sanitized configuration
- host status snapshot
- system/runtime information with machine/user names redacted
- a consistent event snapshot reconstructed into a new database with supported secret arguments masked
- fallback diagnostic log

Supported password/passwd/token/api-key/api_key/secret arguments are masked for whitespace-separated, equals-separated and quoted values in configuration, structured event strings and fallback export. This also applies to historical event data; arbitrary secret formats are not guaranteed to be recognized. Failed database extraction is reported without copying the raw database.

## 14. v2 candidates

- Persistent pause state resilient to watcher crashes
- HTTP/TCP health checks
- Windows toast notifications
- Home Assistant webhook notifications
- Optional resource threshold checks
- Child process observation without ownership
- Diagnostic event export independent of raw SQLite files

## Reliability and retention

Configuration updates use a process-wide file lease and merge only the edited fields into the latest file. Full validation precedes reload; running or recovering targets must be explicitly stopped before changing executable or privilege. Unchanged targets retain their process handles and state.

Retention runs after startup and hourly without blocking supervision. The database capacity setting is a soft target (100 MiB default, minimum 10 MiB); oldest rows are deleted in batches toward 90% used-page capacity when exceeded. Checkpoint/VACUUM are attempted only on capacity overflow. Contention may defer physical shrinking and WAL can temporarily exceed the target. Fallback logs rotate at 5 MiB, retaining the current file and two older generations.
