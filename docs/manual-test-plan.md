# Manual test plan

These tests are release blockers for the first stable build.

## A. TVRock / TVTest compatibility

1. Start TVRock manually from Explorer and record normal behavior.
2. Stop TVRock.
3. Add TVRock to AppWatcher as a normal-user application.
4. Start it from AppWatcher.
5. Cause TVRock to launch TVTest in the same way used in normal operation.
6. Confirm TVTest is visible on the logged-on desktop and interactive.
7. Confirm TVTest has the expected user/session.
8. Confirm AppWatcher stopping/restarting TVRock does not automatically terminate TVTest.

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
2. Add Libre Hardware Monitor as Administrator.
3. Confirm Elevated helper starts at high integrity without an ongoing visible window.
4. Confirm LHM launches without a UAC prompt on every restart.
5. Confirm LHM remains visible in the user's desktop session.

## H. Windows shutdown/logoff

1. Run at least one monitored application.
2. Log off or shut down Windows.
3. Confirm AppWatcher does not fight shutdown by relaunching targets.

## I. Config corruption

1. Back up the data directory.
2. Corrupt `config.json` while a valid backup exists.
3. Restart hosts.
4. Confirm backup recovery occurs and a `ConfigurationRecovery` fallback record is written.

## J. SQLite failure tolerance

1. Make event database temporarily unwritable in a test environment.
2. Cause a monitored target to exit.
3. Confirm supervision/restart still occurs.
4. Confirm fallback diagnostic logging is attempted.
