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
3. subscribes to process exit events;
4. runs a periodic window responsiveness loop only when enabled;
5. evaluates restart policy and loop protection after an unexpected exit;
6. logs the event, decision, reason and result.

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

`ConfigService` performs serialized reads/writes, rotates three backups, writes to a temporary file and then replaces the live JSON file.

Configuration has an explicit schema version and migration pipeline. Older supported schemas are upgraded before use, while a configuration from a newer unsupported schema is rejected to avoid destructive downgrade behavior.

Both supervisor hosts filter the same configuration by `PrivilegeLevel`.

## Event storage

`EventStore` uses Microsoft.Data.Sqlite. It enables WAL and `synchronous=NORMAL` and indexes timestamp and `(application_id, timestamp)`.

`ResilientEventSink` treats log database failures as non-fatal.

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
