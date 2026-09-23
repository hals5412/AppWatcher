# Architecture

## Process model

```text
Interactive Windows user session

  AppWatcher.Agent.exe (medium integrity)
       ├─ normal target A
       └─ normal target B
            └─ GUI child           <- unmanaged by AppWatcher

  AppWatcher.Elevated.exe (high integrity)
       └─ administrator target

  AppWatcher.UI.exe (on demand)
       ├─ Named Pipe -> Agent
       └─ Named Pipe -> Elevated
```

Both background hosts are started with Task Scheduler using `TASK_LOGON_INTERACTIVE_TOKEN`. The elevated task has the highest run level.

This intentionally avoids a Windows service because service-launched GUI processes live in Session 0 and cannot behave like normal desktop applications.

## Process supervision

Each configured application is represented by an `ApplicationSupervisor`.

The supervisor:

1. optionally finds an existing exact executable-path match;
2. attaches a process handle;
3. when `AttachExisting` is enabled, checks for a matching external process every five seconds while no process is attached;
4. subscribes to process exit events;
5. runs a periodic window responsiveness loop only when enabled;
6. evaluates restart policy and loop protection after an unexpected exit;
7. logs the event, decision, reason and result.

External discovery queries only the executable names configured for unattached targets and then verifies the full path. It does not inspect every process on each interval. Manual Stop suppresses discovery for that target until an explicit Start or Restart.

`InteractiveProcessLauncher` uses normal interactive shell execution. No Job Object is assigned.

## IPC

The UI sends framed JSON messages over two per-user named pipes:

```text
AppWatcher.Agent.<SID>
AppWatcher.Elevated.<SID>
```

Messages are length-prefixed rather than newline-delimited so JSON formatting cannot break framing.

Current commands include snapshot, reload, start, stop, restart, pause/resume and maintenance mode.

## Configuration

`ConfigService` serializes reads, migration and writes across processes with an exclusive file lease (10-second acquisition timeout). It rotates three readable backups, uses a unique same-directory temporary file and replaces the live JSON file. `UpdateAsync` applies UI changes to the latest configuration while holding the lease.

Configuration has an explicit schema version and migration pipeline. Older supported schemas are upgraded before use, while a configuration from a newer unsupported schema is rejected to avoid destructive downgrade behavior.

Both supervisor hosts filter the same configuration by `PrivilegeLevel`.

## Event storage

`EventStore` uses Microsoft.Data.Sqlite. It enables WAL and `synchronous=NORMAL` and indexes timestamp and `(application_id, timestamp)`.

`ResilientEventSink` uses a bounded 1024-record queue so supervision does not wait for SQLite I/O. Database failures, including initialization failures, are non-fatal; retries are throttled to one minute. Overflow is summarized in the rotating fallback log. An hourly, file-lease-coordinated maintenance task enforces retention and a soft capacity target.

## Self-monitoring

Every host snapshot exposes:

- host PID
- host start time
- host uptime
- host working set
- version

This information is gathered only when a snapshot is requested, rather than written continuously.

## Security boundary

The normal Agent does not try to elevate individual targets. Administrator applications belong to the already-elevated helper.

The helper is elevated at logon through Task Scheduler, which avoids repeated UAC prompts on application restarts.

Named-pipe servers use an explicit protected DACL that grants access to LocalSystem, built-in Administrators, and the current Windows user SID. No mandatory-integrity SACL is added, allowing the medium-integrity UI and the high-integrity helper for the same user to communicate while avoiding reliance on the process-default pipe ACL.

## State preservation and tests

Reload validates the complete candidate before applying changes by application ID. Existing supervisors retain manual-stop intent, pause deadlines and restart history. Executable/privilege changes require explicit Stop. Normal and elevated hosts both observe the previous configuration before UI privilege transfer; the destination registers the application stopped.

Per-application operations and exit callbacks share a gate. Delayed recovery rechecks generation, pause and shutdown before launching. Background tasks are tracked and joined on disposal; snapshots are immutable published values. `IProcessRuntime`, `IManagedProcess` and `TimeProvider` permit deterministic tests without launching production targets.

The existing ReloadConfiguration IPC command accepts an optional ConfigurationPreview for validation without applying it. All binaries should be replaced together; mixed-version hosts are not a supported upgrade state.

Diagnostic export reads a consistent SELECT snapshot into a fresh sanitized database. It never copies raw database bytes on failure. Acquisition failures are listed in collection-notes.txt.
