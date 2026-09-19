# Manual test plan

These tests are release blockers for the first stable build.

## A. Interactive parent / child GUI compatibility

1. Choose a normal desktop application that launches a separate GUI child application during normal use.
2. Start the parent manually from Explorer and record normal behavior.
3. Stop the parent.
4. Add the parent to AppWatcher as a normal-user application.
5. Start it from AppWatcher.
6. Cause the parent to launch its GUI child in the same way used during normal operation.
7. Confirm the child is visible and interactive on the logged-on desktop.
8. Confirm the child runs under the expected user/session.
9. Confirm AppWatcher stopping or restarting the parent does not automatically terminate the child while `ChildProcessPolicy` is `Unmanaged`.

Pass criterion: launch behavior is materially equivalent to manual interactive launch.

## B. Normal process exit

1. Configure restart-on-any-unexpected-exit with 5 s delay.
2. Terminate the target outside AppWatcher.
3. Confirm status changes to Restarting.
4. Confirm target restarts after approximately 5 s.
5. Confirm event log contains ProcessExited -> RestartDecision -> ProcessStarted.

## C. Intentional stop

1. Click Stop in AppWatcher.
2. Confirm target exits.
3. Confirm it stays stopped.
4. Confirm reason code `IntentionalStop` is logged.

## D. Hang detection

Use a disposable test GUI that can intentionally block its UI thread.

1. Set hang interval 5 s and timeout 30 s.
2. Block the UI for 10 s, then recover. Confirm AppWatcher does not kill it.
3. Block for >30 s. Confirm status becomes Unresponsive and the parent is terminated/restarted.
4. Confirm children are not killed.

## E. Restart-loop protection

1. Configure a test target that exits immediately.
2. Use 5 restarts / 10 min.
3. Confirm AppWatcher enters Backoff rather than creating an infinite restart loop.
4. Confirm exact decision and backoff expiry appear in logs.

## F. Maintenance mode

1. Pause one app while it is running.
2. Kill it externally; confirm it does not restart during pause.
3. Resume monitoring; confirm it restarts when StartWithWatcher is enabled.
4. Repeat with global maintenance mode.

## G. Elevated application

1. Install startup tasks.
2. Add a disposable desktop application that requires Administrator privileges.
3. Confirm Elevated Helper starts at high integrity without an ongoing visible window.
4. Confirm the target launches without a UAC prompt on every restart.
5. Confirm the target remains visible in the user's desktop session.

## H. Windows shutdown/logoff

1. Run at least one monitored application.
2. Log off or shut down Windows.
3. Confirm AppWatcher does not fight shutdown by relaunching targets.

## I. Config corruption and migration

1. Back up the data directory.
2. Confirm the active configuration is schema version 2.
3. In a disposable copy, change the schema version to 1 and start AppWatcher.
4. Confirm AppWatcher migrates it to schema 2 and preserves the original as `config.backup-1.json`.
5. Corrupt `config.json` while a valid backup exists.
6. Restart hosts.
7. Confirm backup recovery occurs and a `ConfigurationRecovery` fallback record is written.
8. In a disposable copy, set `schemaVersion` higher than the current supported version.
9. Confirm AppWatcher rejects the newer schema rather than silently replacing it with an older backup.

## J. SQLite failure tolerance

1. Make event database temporarily unwritable in a test environment.
2. Cause a monitored target to exit.
3. Confirm supervision/restart still occurs.
4. Confirm fallback diagnostic logging is attempted.

## K. Startup self-check

1. Start AppWatcher with valid startup tasks and confirm no warning row is shown.
2. Disable the Agent task and confirm a warning appears.
3. Re-enable it, then temporarily change the registered executable path or working directory.
4. Confirm AppWatcher reports the stale registration.
5. Run **Install / repair startup tasks...** and confirm the warning clears.
6. If an administrator target is configured, stop Elevated Helper and confirm the offline warning appears.

## L. Application-scoped event log

1. Generate several events for two monitored applications.
2. Right-click one application and open Logs.
3. Confirm only that application's events are shown.
4. Rename the application and generate another event.
5. Open its scoped logs again and confirm both old-name and new-name events are present.
6. Open the global Logs button and confirm events from all applications and hosts remain visible.

## M. Complete shutdown / binary replacement

1. Start Agent and Elevated Helper and monitor at least one normal and one administrator target.
2. Choose **Exit AppWatcher completely**.
3. Verify `AppWatcher.Agent.exe`, `AppWatcher.Elevated.exe`, and the dashboard exit within 5 seconds.
4. Verify monitored target processes remain running with the same PIDs.
5. Replace AppWatcher binaries and start AppWatcher again; verify existing targets are attached when `AttachExisting` is enabled.
6. Repeat with `AppWatcher.UI.exe --shutdown` and `stop-appwatcher.ps1`.

## N. AppWatcher restart

1. Install the AppWatcher startup tasks.
2. Choose **Restart AppWatcher** from the tray.
3. Verify Agent and Elevated PIDs change while monitored target PIDs remain unchanged.
4. Verify the Elevated Helper returns without a new UAC prompt when the registered task exists.
5. Verify event history contains `HostShutdownRequested` / `HostStopped` for both hosts.

## O. Release package integrity

1. Download both release ZIPs and `SHA256SUMS.txt` from GitHub Releases.
2. Verify each ZIP's SHA256 against the checksum file.
3. Extract the framework-dependent package on a machine with the .NET 10 Desktop Runtime and launch the dashboard.
4. Extract the self-contained package on a clean/disposable Windows environment without relying on an installed .NET runtime and launch the dashboard.
5. Confirm Agent, Elevated Helper, UI, documentation and helper scripts are present.
6. Confirm no user data (`config.json`, SQLite database, fallback log) is included in either ZIP.

## P. Application icon

1. Verify UI, Agent and Elevated executables show the AppWatcher icon in Explorer properties.
2. Verify the dashboard taskbar icon and healthy tray icon use the AppWatcher icon.
3. Inspect 16, 32, 48 and 256 px presentations for clipping or illegibility.

## Q. Reliability regression (isolated Windows user or VM only)

1. Stop target A; pause target B; trigger Backoff on target C. Add/edit/remove another target and verify A remains stopped, B retains its pause deadline, and C retains its restart history and Backoff.
2. With AttachExisting disabled, edit a running target name and verify its PID does not change and no duplicate process appears.
3. Set a timed global maintenance window, let it expire, and verify eligible targets resume but manually stopped targets do not. Repeat after replacing a timed pause with an indefinite pause.
4. Use tray Pause/Resume with both normal and administrator targets. Make one host unavailable and verify partial failure is visible while the successful side keeps its state.
5. Stop while waiting for an automatic restart or Backoff expiry; verify no later launch. Refuse graceful close with forced termination disabled and verify the UI retains the running PID and reports failure.
6. Attempt executable/privilege changes while running; verify rejection. Explicitly Stop, transfer privilege, and verify the destination remains stopped until Start.
7. Deny access to the isolated event database, start the hosts, and verify target supervision and IPC continue. Restore access and verify DB logging can recover after the retry interval.
8. Create a diagnostic ZIP containing only dummy token/password values. Inspect configuration, reconstructed DB and fallback files for residual secrets; verify the original historical database is unchanged.
9. Repeat startup, coordinated shutdown and application restart using both distribution formats. Record manual results separately from automated test results.

The automated suite uses fake processes and a controllable clock, plus a separate worker for cross-process configuration tests. It does not establish real WinForms, UAC, Task Scheduler or high/medium-integrity interoperability. Those checks remain manual release blockers until recorded on an isolated environment.