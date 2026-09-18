# Changelog

## 0.1.0-alpha.18

- Added a tag-driven GitHub Release workflow for `v*` tags.
- Release tags are validated against `Directory.Build.props` before packaging.
- Releases build and test the tagged source before creating downloadable assets.
- Each release publishes both framework-dependent and self-contained win-x64 ZIP packages.
- Added `SHA256SUMS.txt` to every GitHub Release for package integrity verification.
- Alpha, beta and release-candidate versions are automatically marked as GitHub prereleases.
- Extended the publish script with package suffix support so multiple package variants can be produced without overwriting each other.
## 0.1.0-alpha.17

- Added application-scoped event-log windows from the dashboard row context menu.
- Selected-application logs now query SQLite by stable application ID instead of filtering only by display name.
- Scoped log windows continue to show historical events recorded before an application was renamed.
- The global Logs button remains unchanged and continues to show/filter events across all applications and hosts.
- Scoped log windows show the selected application in the title and lock the application field to make the active scope clear.
## 0.1.0-alpha.16

- Added an automatic startup self-check that surfaces problems only when action is needed.
- The dashboard now checks Agent/Elevated binaries, startup-task registration, enabled state, executable path and working directory.
- Agent connectivity is checked continuously; Elevated Helper connectivity is flagged when enabled administrator applications require it.
- Startup-task inspection is cached for 15 seconds to avoid querying Task Scheduler on every two-second dashboard refresh.
- Added a collapsible warning row with click-through diagnostics and repair guidance.
- Expanded the existing startup-task status dialog to show and validate the registered working directory.
## 0.1.0-alpha.15

- Added a versioned configuration migration pipeline and advanced the configuration schema to version 2.
- Schema 1 and pre-versioned configuration files are automatically upgraded to schema 2 when loaded.
- The exact pre-migration config is preserved as `config.backup-1.json` before the upgraded configuration is written atomically.
- Configuration saves always stamp the current schema version and normalize required collections, strings and application IDs.
- A configuration created by a newer AppWatcher schema is rejected explicitly instead of silently falling back to an older backup.
- Added regression tests for schema-1 migration, pre-versioned migration, future-schema rejection and current-version stamping.
## 0.1.0-alpha.14

- Added the first automated test project for AppWatcher.Core.
- Added restart-loop/backoff tests covering allowed restarts, backoff entry, active backoff, expiry, healthy reset and disabled loop protection.
- Added configuration tests covering round-trip persistence, three-generation backup rotation, corrupt-primary recovery, first-run creation and validation.
- Made ConfigService accept an optional data directory so automated tests use isolated temporary files instead of the real AppWatcher profile.
- Added `dotnet test` to the Windows GitHub Actions build before packaging.
- Added .NET 10 Microsoft.Testing.Platform runner configuration for local and CI `dotnet test`.
## 0.1.0-alpha.13

- Added a running-application picker to the dashboard.
- The elevated helper enumerates executable paths in the current interactive session and detects whether each process is running normally or elevated.
- Running processes are grouped by executable path and privilege, with PID, instance count, window title and registration status shown in the picker.
- Selecting a running application pre-fills the normal application editor with executable path, working directory, privilege and attach-existing settings.
- Existing monitored executable paths are marked as already registered and cannot be selected again.
## 0.1.0-alpha.12

- Made dashboard and row-context commands state-aware.
- Disabled Start for already-running, paused, transitioning or unavailable applications.
- Disabled Stop when no process is running and disabled Restart unless a process is currently running.
- Disabled per-application Resume when the application is not paused, monitoring is disabled, or global maintenance is active.
- Command availability now updates immediately on selection changes, right-click selection and the two-second dashboard refresh.
## 0.1.0-alpha.11

- Removed the manual Refresh command from the dashboard and row context menu.
- The dashboard continues to refresh automatically every two seconds while it is open.
## 0.1.0-alpha.10

- Added a right-click context menu to monitored-application rows.
- The row context menu mirrors the dashboard command bar: Add, Edit, Delete, Start, Stop, Restart, Pause durations, Resume, Logs and Refresh.
- Right-clicking a row now selects that row before showing commands, preventing actions from targeting a previously selected application.
## 0.1.0-alpha.9

- Prevented application-editor field labels from wrapping at awkward positions.
- Widened the field-label column while retaining full-width path controls.
- Removed product-specific names from child-process policy help text.
## 0.1.0-alpha.8

- Replaced the sparse Settings tabs with one compact settings page.
- Expanded executable and working-directory fields in the application editor.
- Reduced wasted horizontal space by narrowing the application-editor label column.
- Improved button sizing, padding and DPI-friendly spacing across both dialogs.
- Added full-path tooltips for executable and working-directory fields.
## 0.1.0-alpha.7

- Increased the default UI font and dashboard control/row sizing for better readability.
- Persisted dashboard column widths and order in config.json.
- Added Tools > Startup task status to verify Agent/Elevated registration, state, executable path and last task result.
- Localized the ProcessStarted reason shown in the dashboard.
- Removed product-specific monitoring examples from the English and Japanese READMEs.
## 0.1.0-alpha.6

- Fixed IPC startup regression that could leave both Agent and Elevated Helper shown as offline while their processes were running.
- Removed the mandatory-integrity SACL from the named pipe and retained a current-user DACL for cross-UAC communication.
- Added fallback logging for named-pipe server creation failures so IPC faults are diagnosable without stopping supervision.
## 0.1.0-alpha.5

- Prevented duplicate application registrations by normalized executable path.
- Fixed cross-UAC IPC so the medium-integrity dashboard can communicate with the elevated helper securely.
- Added detailed Elevated Helper startup diagnostics.
- Localized offline/pending dashboard event and reason codes.
## 0.1.0-alpha.4

- Fixed the dashboard layout so column headers are always visible.
- Dashboard columns can now be resized and reordered, with horizontal scrolling and cell tooltips.
- Fixed clipped Japanese labels and check boxes in application/settings dialogs.
- Configured applications remain visible even when their monitoring host is offline.
- Adding an administrator application now starts Elevated Helper via Task Scheduler when possible, with UAC fallback when necessary.
- Reduced configuration reload delays when a host is offline.

## 0.1.0-alpha.3

- Added coordinated full shutdown of Agent and Elevated helper without terminating monitored applications.
- Added AppWatcher background-component restart from the tray and dashboard.
- Added `--shutdown` / `--restart` command-line lifecycle controls and PowerShell helper scripts.
- Added an application icon and embedded it in all Windows executables.
- Added host-shutdown decision logging and Japanese/English lifecycle UI strings.

## 0.1.0-alpha.2

- Added Japanese / English localization with automatic Windows display-language selection.
- Added a Settings dialog for language selection and event retention.
- Localized dashboard, application editor, event log, tray menu, startup setup, validation messages, state names and common event/reason codes.
- Event-log details preserve the original event/reason codes while displaying localized descriptions.

## 0.1.0-alpha.1

Initial implementation scaffold:

- normal and elevated interactive monitoring hosts;
- combined WinForms dashboard;
- full-path attach/start process supervision;
- exit restart and restart-loop protection;
- GUI hang detection;
- per-app pause and global maintenance mode;
- SQLite structured event logging with reason codes;
- configuration backups;
- Task Scheduler startup installation;
- diagnostic ZIP generation;
- explicit TVRock -> TVTest interactive-child compatibility design.
