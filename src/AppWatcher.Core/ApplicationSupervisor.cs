using System.Diagnostics;

namespace AppWatcher.Core;

public sealed class ApplicationSupervisor : IAsyncDisposable
{
    private ApplicationDefinition _definition;
    private readonly IProcessRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly IEventSink _events;
    private readonly Func<bool> _globalPauseActive;
    private readonly Func<bool> _shutdownActive;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RestartLimiter _restartLimiter;
    private readonly CancellationTokenSource _lifetime = new();

    private IManagedProcess? _process;
    private CancellationTokenSource? _hangMonitorCts;
    private DateTimeOffset? _processStartedUtc;
    private DateTimeOffset? _pauseUntilUtc;
    private bool _pauseIndefinite;
    private int _pauseGeneration;
    private int _restartGeneration;
    private bool _pendingRecovery;
    private bool _pendingStartup;
    private int? _lastExitCode;
    private readonly object _tasksLock = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private Task _hangTask = Task.CompletedTask;
    private ApplicationSnapshot _snapshot;
    private bool _intentionalStop;
    private bool _disposed;
    private DateTimeOffset _nextExistingDiscoveryFailureLogUtc;
    private AppRuntimeState _state = AppRuntimeState.Unknown;
    private string _lastEvent = "Initialized";
    private string? _lastReason;

    public ApplicationSupervisor(
        ApplicationDefinition definition,
        ProcessMatcher matcher,
        InteractiveProcessLauncher launcher,
        IEventSink events,
        Func<bool> globalPauseActive,
        Func<bool> shutdownActive)
        : this(definition, new WindowsProcessRuntime(), events, globalPauseActive, shutdownActive) { }

    public ApplicationSupervisor(ApplicationDefinition definition, IProcessRuntime runtime,
        IEventSink events, Func<bool> globalPauseActive, Func<bool> shutdownActive, TimeProvider? time = null)
    {
        _definition = Clone(definition);
        _runtime = runtime;
        _time = time ?? TimeProvider.System;
        _events = events;
        _globalPauseActive = globalPauseActive;
        _shutdownActive = shutdownActive;
        _restartLimiter = new RestartLimiter(_definition);
        _snapshot = BuildSnapshot();
    }

    public Guid Id => _definition.Id;
    public ApplicationDefinition Definition => Clone(_definition);
    private static ApplicationDefinition Clone(ApplicationDefinition definition) =>
        System.Text.Json.JsonSerializer.Deserialize<ApplicationDefinition>(System.Text.Json.JsonSerializer.Serialize(definition, JsonDefaults.Options), JsonDefaults.Options)!;

    public async Task ValidateUpdateAsync(ApplicationDefinition next, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ValidateUpdateLocked(next); }
        finally { _gate.Release(); }
    }

    private void ValidateUpdateLocked(ApplicationDefinition next)
    {
        ThrowIfDisposed();
        if (next.Id != Id) throw new InvalidOperationException("Application ID cannot change.");
        if (IdentityChanged(_definition, next) && (!_intentionalStop || (_process is not null && !HasExited(_process)) || _pendingRecovery))
            throw new InvalidOperationException("Stop the application explicitly before changing its executable or privilege.");
    }

    public static bool IdentityChanged(ApplicationDefinition old, ApplicationDefinition next) =>
        old.Privilege != next.Privilege || !string.Equals(Path.GetFullPath(old.ExecutablePath), Path.GetFullPath(next.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    public async Task UpdateAsync(ApplicationDefinition next, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateUpdateLocked(next);
            if (System.Text.Json.JsonSerializer.Serialize(_definition, JsonDefaults.Options) == System.Text.Json.JsonSerializer.Serialize(next, JsonDefaults.Options)) return;
            StopHangMonitor();
            await _hangTask.ConfigureAwait(false);
            _definition = Clone(next);
            _restartLimiter.UpdateDefinition(_definition);
            if (_process is not null && !HasExited(_process)) StartHangMonitor(_process);
            if (IsPausedEffective) { ++_restartGeneration; SetState(AppRuntimeState.Paused, "MonitoringPaused", "Configuration"); }
            else if (_process is not null && !HasExited(_process)) SetState(AppRuntimeState.Healthy, "Attached", "Configuration");
        }
        finally { Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _pendingStartup = _definition.StartWithWatcher;

            if (_definition.AttachExisting)
            {
                var existing = _runtime.FindExisting(_definition);
                if (existing is not null)
                {
                    await AttachProcessLockedAsync(existing, "ExistingProcessAttached", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (_definition.StartWithWatcher)
            {
                await StartProcessLockedAsync("WatcherStartup", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetState(AppRuntimeState.Stopped, "NotStartedByPolicy", "StartupPolicy");
            }
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task<bool> TryAttachExistingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !_definition.AttachExisting || _intentionalStop || _shutdownActive()) return false;
            if (_process is not null && !HasExited(_process)) return false;

            IManagedProcess? existing;
            try
            {
                existing = _runtime.FindExisting(_definition);
            }
            catch (Exception ex)
            {
                await LogExistingDiscoveryFailureAsync(ex, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (existing is null) return false;

            try
            {
                await AttachProcessLockedAsync(existing, "ExistingProcessAttached", cancellationToken).ConfigureAwait(false);
                // An externally launched instance supersedes any delayed automatic start.
                ++_restartGeneration;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_process, existing)) DetachCurrentProcess();
                else existing.Dispose();
                _processStartedUtc = null;
                SetState(AppRuntimeState.Failed, "ProcessAttachFailed", "ExternalProcessDiscovery");
                await LogExistingDiscoveryFailureAsync(ex, cancellationToken).ConfigureAwait(false);
                return false;
            }
            return true;
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot());
            _gate.Release();
        }
    }

    private async Task LogExistingDiscoveryFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now < _nextExistingDiscoveryFailureLogUtc) return;
        _nextExistingDiscoveryFailureLogUtc = now.AddMinutes(1);
        await WriteEventAsync(AppLogLevel.Warning, "ExistingProcessDiscoveryFailed", "ProcessDiscoveryFailed",
            new { exceptionType = exception.GetType().Name, exception.Message }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(string reason = "ManualStart", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _intentionalStop = false;
            if (_process is not null && !HasExited(_process))
            {
                SetState(IsPausedEffective ? AppRuntimeState.Paused : AppRuntimeState.Healthy, "AlreadyRunning", reason);
                return;
            }

            _intentionalStop = false;
            ++_restartGeneration;
            _pendingRecovery = false;
            _pendingStartup = true;
            await StartProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task StopAsync(string reason = "ManualStop", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _intentionalStop = true;
            ++_restartGeneration;
            _pendingRecovery = false;
            _pendingStartup = false;
            await StopProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
            if (_process is not null && !HasExited(_process))
            {
                SetState(AppRuntimeState.Failed, "ProcessStopFailed", reason);
                throw new InvalidOperationException("Target is still running. Automatic restart remains suppressed.");
            }
            SetState(AppRuntimeState.Stopped, "IntentionalStop", reason);
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task RestartAsync(string reason = "ManualRestart", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _intentionalStop = true;
            await StopProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
            if (_process is not null && !HasExited(_process))
            {
                SetState(AppRuntimeState.Failed, "ProcessStopFailed", reason);
                throw new InvalidOperationException("Target is still running. Restart was not performed.");
            }
            DetachCurrentProcess();
            _intentionalStop = false;
            SetState(AppRuntimeState.Restarting, "ManualRestart", reason);
            _pendingRecovery = true;
            _pendingStartup = true;
            ScheduleRestartLocked(reason);
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var generation = ++_pauseGeneration;
            ++_restartGeneration;
            if (duration is null)
            {
                _pauseIndefinite = true;
                _pauseUntilUtc = null;
            }
            else
            {
                _pauseIndefinite = false;
                _pauseUntilUtc = _time.GetUtcNow().Add(duration.Value);
                _ = Track(() => ResumeAfterDelayAsync(duration.Value, generation, _lifetime.Token));
            }

            SetState(AppRuntimeState.Paused, "ApplicationPaused", duration is null ? "Indefinite" : duration.Value.ToString());
            await WriteEventAsync(AppLogLevel.Information, "MonitoringPaused", "ApplicationPaused", new
            {
                durationSeconds = duration?.TotalSeconds,
                pauseUntilUtc = _pauseUntilUtc
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ++_pauseGeneration;
            _pauseIndefinite = false;
            _pauseUntilUtc = null;

            await WriteEventAsync(AppLogLevel.Information, "MonitoringResumed", "UserResume", null, cancellationToken).ConfigureAwait(false);

            if (_globalPauseActive() || !_definition.MonitoringEnabled)
            {
                SetState(AppRuntimeState.Paused, "GlobalOrConfigPauseStillActive", "ResumeDeferred");
                return;
            }

            if (_process is not null && !HasExited(_process))
            {
                SetState(AppRuntimeState.Healthy, "MonitoringResumed", "ProcessStillRunning");
            }
            else if (!_intentionalStop && (_pendingStartup || (_pendingRecovery && PolicyAllowsRestart())))
            {
                await RecoverPendingLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetState(AppRuntimeState.Stopped, "MonitoringResumed", "StartWithWatcherDisabled");
            }
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public async Task OnGlobalPauseChangedAsync(bool active, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            if (active)
            {
                ++_restartGeneration;
                SetState(AppRuntimeState.Paused, "GlobalMaintenance", "GlobalPause");
                return;
            }

            if (IsApplicationPaused || !_definition.MonitoringEnabled)
            {
                SetState(AppRuntimeState.Paused, "ApplicationPauseStillActive", "GlobalResume");
                return;
            }

            if (_process is not null && !HasExited(_process))
            {
                SetState(AppRuntimeState.Healthy, "GlobalMaintenanceEnded", "ProcessStillRunning");
            }
            else if (!_intentionalStop && (_pendingStartup || (_pendingRecovery && PolicyAllowsRestart())))
            {
                await RecoverPendingLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetState(AppRuntimeState.Stopped, "GlobalMaintenanceEnded", "StartWithWatcherDisabled");
            }
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    public ApplicationSnapshot Snapshot()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot with
        {
            Uptime = snapshot.ProcessId is not null && snapshot.ProcessStartedUtc is { } started ? _time.GetUtcNow() - started : null,
            RestartCountInWindow = _restartLimiter.CountAt(_time.GetUtcNow())
        };
    }

    private ApplicationSnapshot BuildSnapshot()
    {
        var now = _time.GetUtcNow();
        var process = _process;
        int? pid = null;
        TimeSpan? uptime = null;
        if (process is not null && !HasExited(process))
        {
            pid = process.Id;
            if (_processStartedUtc is not null)
            {
                uptime = now - _processStartedUtc.Value;
            }
        }

        return new ApplicationSnapshot(
            _definition.Id,
            _definition.Name,
            _definition.Privilege,
            _intentionalStop ? (pid is null ? AppRuntimeState.Stopped : AppRuntimeState.Failed) : IsPausedEffective && _state is not AppRuntimeState.Backoff ? AppRuntimeState.Paused : _state,
            pid,
            _processStartedUtc,
            uptime,
            _restartLimiter.CountAt(_time.GetUtcNow()),
            _pauseUntilUtc,
            _pauseIndefinite,
            _lastEvent,
            _intentionalStop && pid is null ? "IntentionalStop" : _lastReason,
            _definition.ExecutablePath,
            _definition.MonitoringEnabled,
            _definition.DetectHangs);
    }

    private bool IsApplicationPaused => _pauseIndefinite || _pauseUntilUtc is not null;
    private bool IsPausedEffective => IsApplicationPaused || _globalPauseActive() || !_definition.MonitoringEnabled;

    private async Task StartProcessLockedAsync(string reason, CancellationToken cancellationToken)
    {
        if (_intentionalStop || _disposed) return;
        if (_shutdownActive())
        {
            SetState(AppRuntimeState.Stopped, "WindowsShutdownInProgress", reason);
            await WriteEventAsync(AppLogLevel.Information, "StartSuppressed", "WindowsShutdownInProgress", null, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (IsPausedEffective)
        {
            SetState(AppRuntimeState.Paused, "MonitoringPaused", reason);
            return;
        }

        if (_process is not null && !HasExited(_process)) return;

        SetState(AppRuntimeState.Starting, "StartRequested", reason);
        _pendingStartup = false;
        await WriteEventAsync(AppLogLevel.Information, "ProcessStartRequested", reason, new
        {
            _definition.ExecutablePath,
            _definition.Arguments,
            workingDirectory = _definition.EffectiveWorkingDirectory,
            privilege = _definition.Privilege.ToString(),
            launchMode = "Interactive",
            childProcesses = _definition.ChildProcessPolicy.ToString()
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var process = _runtime.Launch(_definition);
            await AttachProcessLockedAsync(process, "ProcessStarted", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(AppRuntimeState.Failed, "ProcessStartFailed", ex.GetType().Name);
            await WriteEventAsync(AppLogLevel.Error, "ProcessStartFailed", ex.GetType().Name, new
            {
                ex.Message,
                hResult = ex.HResult
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AttachProcessLockedAsync(IManagedProcess process, string reason, CancellationToken cancellationToken)
    {
        DetachCurrentProcess();
        _process = process;
        _pendingRecovery = false;
        _pendingStartup = false;

        try
        {
            _processStartedUtc = process.StartedUtc;
        }
        catch
        {
            _processStartedUtc = _time.GetUtcNow();
        }

        process.Exited += ProcessOnExited;
        process.ObserveExit();
        SetState(IsPausedEffective ? AppRuntimeState.Paused : AppRuntimeState.Healthy, reason, "Attached");

        await WriteEventAsync(AppLogLevel.Information, reason, reason, new { processId = process.Id, _definition.ExecutablePath }, cancellationToken).ConfigureAwait(false);
        StartHangMonitor(process);
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        Track(() => HandleProcessExitedAsync(sender as IManagedProcess));
    }

    private async Task HandleProcessExitedAsync(IManagedProcess? exitedProcess)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (exitedProcess is null || !ReferenceEquals(exitedProcess, _process)) return;

            var exitCode = SafeExitCode(exitedProcess);
            _lastExitCode = exitCode;
            _pendingRecovery = !_intentionalStop && PolicyAllowsRestart();
            var uptime = _processStartedUtc is null ? TimeSpan.Zero : _time.GetUtcNow() - _processStartedUtc.Value;
            if (uptime >= TimeSpan.FromMinutes(Math.Max(1, _definition.HealthyResetMinutes)))
            {
                _restartLimiter.ResetHealthy();
            }

            StopHangMonitor();
            DetachCurrentProcess();
            _processStartedUtc = null;

            await WriteEventAsync(
                exitCode is 0 ? AppLogLevel.Information : AppLogLevel.Warning,
                "ProcessExited",
                _intentionalStop ? "IntentionalStop" : "UnexpectedProcessExit",
                new { exitCode, uptimeSeconds = uptime.TotalSeconds },
                CancellationToken.None).ConfigureAwait(false);

            if (_shutdownActive())
            {
                SetState(AppRuntimeState.Stopped, "WindowsShutdownInProgress", "ProcessExited");
                await WriteEventAsync(AppLogLevel.Information, "RestartDecision", "WindowsShutdownInProgress", new { decision = "NoAction" }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (_intentionalStop)
            {
                SetState(AppRuntimeState.Stopped, "IntentionalStop", "NoRestart");
                await WriteEventAsync(AppLogLevel.Information, "RestartDecision", "IntentionalStop", new { decision = "NoAction" }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (IsPausedEffective)
            {
                SetState(AppRuntimeState.Paused, "MonitoringPaused", "NoRestart");
                await WriteEventAsync(AppLogLevel.Information, "RestartDecision", "MonitoringPaused", new { decision = "NoAction" }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var shouldRestart = _definition.RestartPolicy switch
            {
                RestartPolicy.AnyUnexpectedExit => true,
                RestartPolicy.AbnormalExitOnly => exitCode is null || exitCode != 0,
                _ => false
            };

            if (!shouldRestart)
            {
                SetState(AppRuntimeState.Stopped, "RestartPolicyDeclined", _definition.RestartPolicy.ToString());
                await WriteEventAsync(AppLogLevel.Information, "RestartDecision", "RestartPolicyDeclined", new
                {
                    exitCode,
                    policy = _definition.RestartPolicy.ToString(),
                    decision = "NoAction"
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var permission = _restartLimiter.TryAcquire(_time.GetUtcNow());
            if (!permission.Allowed)
            {
                SetState(AppRuntimeState.Backoff, permission.ReasonCode, "RestartSuppressed");
                await WriteEventAsync(AppLogLevel.Warning, "RestartDecision", permission.ReasonCode, new
                {
                    decision = "Backoff",
                    permission.BackoffUntilUtc,
                    attempts = _restartLimiter.AttemptsInWindow,
                    max = _definition.MaxRestarts
                }, CancellationToken.None).ConfigureAwait(false);
                if (permission.BackoffUntilUtc is not null)
                {
                    var generation = ++_restartGeneration;
                    _ = Track(() => ResumeAfterBackoffAsync(permission.BackoffUntilUtc.Value, generation, _lifetime.Token));
                }
                return;
            }

            SetState(AppRuntimeState.Restarting, "UnexpectedProcessExit", "RestartScheduled");
            await WriteEventAsync(AppLogLevel.Information, "RestartDecision", "UnexpectedProcessExit", new
            {
                decision = "Restart",
                delaySeconds = _definition.RestartDelaySeconds,
                attempt = _restartLimiter.AttemptsInWindow,
                max = _definition.MaxRestarts
            }, CancellationToken.None).ConfigureAwait(false);

            ScheduleRestartLocked("AutomaticRestart");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
        }
    }

    private async Task StopProcessLockedAsync(string reason, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || HasExited(process))
        {
            DetachCurrentProcess();
            return;
        }

        await WriteEventAsync(AppLogLevel.Information, "ProcessStopRequested", reason, new
        {
            processId = process.Id,
            gracefulTimeoutSeconds = _definition.GracefulShutdownSeconds,
            forceKill = _definition.ForceKillAfterTimeout,
            childProcesses = "Unmanaged"
        }, cancellationToken).ConfigureAwait(false);

        var closeRequested = false;
        try
        {
            closeRequested = process.CloseMainWindow();
        }
        catch
        {
            closeRequested = false;
        }

        if (closeRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _definition.GracefulShutdownSeconds)));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to optional force kill.
            }
        }

        if (!HasExited(process) && _definition.ForceKillAfterTimeout)
        {
            try
            {
                // Intentionally do not kill the process tree. Child processes are unmanaged by default.
                process.Kill();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await WriteEventAsync(AppLogLevel.Warning, "ProcessForceKilled", "GracefulShutdownTimedOut", new { processId = process.Id }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteEventAsync(AppLogLevel.Error, "ProcessStopFailed", ex.GetType().Name, new { ex.Message }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void StartHangMonitor(IManagedProcess process)
    {
        StopHangMonitor();
        if (!_definition.DetectHangs) return;

        _hangMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _hangMonitorCts.Token;
        _hangTask = Track(() => HangMonitorLoopAsync(process, token));
    }

    private async Task HangMonitorLoopAsync(IManagedProcess process, CancellationToken cancellationToken)
    {
        DateTimeOffset? unresponsiveSince = null;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _definition.HangCheckIntervalSeconds));
        using var timer = new PeriodicTimer(interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_disposed || cancellationToken.IsCancellationRequested || !ReferenceEquals(_process, process) || HasExited(process)) return;
                    if (_intentionalStop || IsPausedEffective || _shutdownActive())
                    {
                        unresponsiveSince = null;
                        continue;
                    }
                    if (_processStartedUtc is not null && _time.GetUtcNow() - _processStartedUtc < TimeSpan.FromSeconds(Math.Max(0, _definition.StartupGraceSeconds)))
                    {
                        continue;
                    }

                    bool? responsive;
                    try { responsive = process.IsWindowResponsive(); }
                    catch { continue; }
                    if (responsive is null) { unresponsiveSince = null; continue; }
                    if (responsive == true)
                    {
                        if (_state == AppRuntimeState.Unresponsive)
                        {
                            SetState(AppRuntimeState.Healthy, "WindowResponsiveAgain", "HangCheck");
                            await WriteEventAsync(AppLogLevel.Information, "WindowResponsive", "RecoveredBeforeTimeout", null, cancellationToken).ConfigureAwait(false);
                        }
                        unresponsiveSince = null;
                        continue;
                    }

                    if (unresponsiveSince is null)
                    {
                        unresponsiveSince = _time.GetUtcNow();
                        await WriteEventAsync(AppLogLevel.Warning, "WindowUnresponsiveObserved", "WindowNotResponding", new
                        {
                            processId = process.Id,
                            timeoutSeconds = _definition.HangTimeoutSeconds
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    SetState(AppRuntimeState.Unresponsive, "WindowNotResponding", "HangCheck");

                    if (_time.GetUtcNow() - unresponsiveSince.Value < TimeSpan.FromSeconds(Math.Max(1, _definition.HangTimeoutSeconds)))
                    {
                        continue;
                    }

                    await WriteEventAsync(AppLogLevel.Warning, "HangDetected", "HangTimeoutExceeded", new
                    {
                        processId = process.Id,
                        timeoutSeconds = _definition.HangTimeoutSeconds
                    }, cancellationToken).ConfigureAwait(false);

                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        await WriteEventAsync(AppLogLevel.Error, "HangRecoveryFailed", ex.GetType().Name, new { ex.Message }, CancellationToken.None).ConfigureAwait(false);
                    }
                    return;
                }
                finally { Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ResumeAfterDelayAsync(TimeSpan duration, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, _time, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || generation != _pauseGeneration) return;
                ++_pauseGeneration;
                _pauseIndefinite = false;
                _pauseUntilUtc = null;
                if (_process is not null && !HasExited(_process)) SetState(AppRuntimeState.Healthy, "MonitoringResumed", "ProcessStillRunning");
                else if (!_intentionalStop && !IsPausedEffective && (_pendingStartup || (_pendingRecovery && PolicyAllowsRestart())))
                    await RecoverPendingLockedAsync(cancellationToken).ConfigureAwait(false);
                else SetState(AppRuntimeState.Stopped, "MonitoringResumed", "NoRestart");
            }
            finally { Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ResumeAfterBackoffAsync(DateTimeOffset untilUtc, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var delay = untilUtc - _time.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _intentionalStop || generation != _restartGeneration || IsPausedEffective || _shutdownActive()) return;
                if (_process is null || HasExited(_process))
                {
                    if (PolicyAllowsRestart())
                    {
                        SetState(AppRuntimeState.Restarting, "BackoffExpired", "AutomaticRecovery");
                        await RecoverPendingLockedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _pendingRecovery = false;
                        SetState(AppRuntimeState.Stopped, "RestartPolicyDeclined", "BackoffExpired");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopHangMonitor()
    {
        var cts = Interlocked.Exchange(ref _hangMonitorCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private void DetachCurrentProcess()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { process.Exited -= ProcessOnExited; } catch { }
        process.Dispose();
    }

    private void SetState(AppRuntimeState state, string reason, string source)
    {
        _state = state;
        _lastEvent = source;
        _lastReason = reason;
    }

    private Task WriteEventAsync(AppLogLevel level, string eventType, string reasonCode, object? details, CancellationToken cancellationToken)
    {
        if (level < _definition.LogLevel) return Task.CompletedTask;
        return _events.WriteAsync(EventRecordFactory.Create(_definition, level, eventType, reasonCode, details), cancellationToken);
    }

    private static bool HasExited(IManagedProcess process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int? SafeExitCode(IManagedProcess process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool PolicyAllowsRestart() => _definition.RestartPolicy == RestartPolicy.AnyUnexpectedExit ||
        (_definition.RestartPolicy == RestartPolicy.AbnormalExitOnly && _lastExitCode != 0);

    private Task RecoverPendingLockedAsync(CancellationToken token)
    {
        if (_pendingStartup) return StartProcessLockedAsync("MonitoringResumed", token);
        var permission = _restartLimiter.TryAcquire(_time.GetUtcNow());
        if (permission.Allowed) ScheduleRestartLocked("AutomaticRestart");
        else if (permission.BackoffUntilUtc is { } until)
        {
            SetState(AppRuntimeState.Backoff, permission.ReasonCode, "RestartSuppressed");
            var generation = ++_restartGeneration;
            Track(() => ResumeAfterBackoffAsync(until, generation, _lifetime.Token));
        }
        return Task.CompletedTask;
    }

    private void ScheduleRestartLocked(string reason)
    {
        var generation = ++_restartGeneration;
        Track(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _definition.RestartDelaySeconds)), _time, _lifetime.Token).ConfigureAwait(false);
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || _intentionalStop || generation != _restartGeneration || IsPausedEffective || _shutdownActive()) return;
                if (!_pendingStartup && !PolicyAllowsRestart()) return;
                await StartProcessLockedAsync(reason, _lifetime.Token).ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release(); }
        });
    }

    private Task Track(Func<Task> operation)
    {
        lock (_tasksLock)
        {
            if (_disposed) return Task.CompletedTask;
            var task = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) when (_disposed) { }
                catch (Exception ex)
                {
                    await _events.WriteAsync(EventRecordFactory.Create(_definition, AppLogLevel.Error,
                        "SupervisorTaskFailed", ex.GetType().Name, new { ex.Message })).ConfigureAwait(false);
                }
            });
            _tasks.Add(task);
            _ = task.ContinueWith(done => { lock (_tasksLock) _tasks.Remove(done); }, TaskScheduler.Default);
            return task;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task[] tasks;
        lock (_tasksLock)
        {
            if (_disposed) return;
            _disposed = true;
            tasks = _tasks.ToArray();
        }
        _lifetime.Cancel();
        StopHangMonitor();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Deliberately detach only. Stopping AppWatcher must not kill managed applications.
            DetachCurrentProcess();
        }
        finally
        {
            Volatile.Write(ref _snapshot, BuildSnapshot()); _gate.Release();
            _lifetime.Dispose();
        }
    }
}
