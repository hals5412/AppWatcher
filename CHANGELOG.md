# Changelog

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
