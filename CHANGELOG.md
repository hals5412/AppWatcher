# Changelog

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
