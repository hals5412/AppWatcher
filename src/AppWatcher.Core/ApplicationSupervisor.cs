using System.Diagnostics;

namespace AppWatcher.Core;

public sealed class ApplicationSupervisor : IAsyncDisposable
{
    private readonly ApplicationDefinition _definition;
    private readonly ProcessMatcher _matcher;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly IEventSink _events;
    private readonly Func<bool> _globalPauseActive;
    private readonly Func<bool> _shutdownActive;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RestartLimiter _restartLimiter;
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    private CancellationTokenSource? _hangMonitorCts;
    private DateTimeOffset? _processStartedUtc;
    private DateTimeOffset? _pauseUntilUtc;
    private bool _pauseIndefinite;
    private int _pauseGeneration;
    private bool _intentionalStop;
    private bool _disposed;
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
    {
        _definition = definition;
        _matcher = matcher;
        _launcher = launcher;
        _events = events;
        _globalPauseActive = globalPauseActive;
        _shutdownActive = shutdownActive;
        _restartLimiter = new RestartLimiter(definition);
    }

    public Guid Id => _definition.Id;
    public ApplicationDefinition Definition => _definition;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!_definition.MonitoringEnabled)
            {
                SetState(AppRuntimeState.Paused, "MonitoringDisabled", "Configuration");
                return;
            }

            if (_globalPauseActive())
            {
                SetState(AppRuntimeState.Paused, "GlobalMaintenance", "Maintenance");
                return;
            }

            if (_definition.AttachExisting)
            {
                var existing = _matcher.FindExisting(_definition);
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
            _gate.Release();
        }
    }

    public async Task StartAsync(string reason = "ManualStart", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is not null && !HasExited(_process))
            {
                SetState(IsPausedEffective ? AppRuntimeState.Paused : AppRuntimeState.Healthy, "AlreadyRunning", reason);
                return;
            }

            _intentionalStop = false;
            await StartProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(string reason = "ManualStop", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _intentionalStop = true;
            await StopProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
            SetState(AppRuntimeState.Stopped, "IntentionalStop", reason);
        }
        finally
        {
            _gate.Release();
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
            _intentionalStop = false;
            SetState(AppRuntimeState.Restarting, "ManualRestart", reason);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _definition.RestartDelaySeconds)), cancellationToken).ConfigureAwait(false);
            await StartProcessLockedAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var generation = ++_pauseGeneration;
            if (duration is null)
            {
                _pauseIndefinite = true;
                _pauseUntilUtc = null;
            }
            else
            {
                _pauseIndefinite = false;
                _pauseUntilUtc = DateTimeOffset.UtcNow.Add(duration.Value);
                _ = ResumeAfterDelayAsync(duration.Value, generation, _lifetime.Token);
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
            _gate.Release();
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
            else if (_definition.StartWithWatcher)
            {
                await StartProcessLockedAsync("MonitoringResumed", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetState(AppRuntimeState.Stopped, "MonitoringResumed", "StartWithWatcherDisabled");
            }
        }
        finally
        {
            _gate.Release();
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
            else if (_definition.StartWithWatcher)
            {
                await StartProcessLockedAsync("GlobalMaintenanceEnded", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetState(AppRuntimeState.Stopped, "GlobalMaintenanceEnded", "StartWithWatcherDisabled");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ApplicationSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
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
            IsPausedEffective && _state is not AppRuntimeState.Backoff ? AppRuntimeState.Paused : _state,
            pid,
            _processStartedUtc,
            uptime,
            _restartLimiter.AttemptsInWindow,
            _pauseUntilUtc,
            _pauseIndefinite,
            _lastEvent,
            _lastReason,
            _definition.ExecutablePath,
            _definition.MonitoringEnabled,
            _definition.DetectHangs);
    }

    private bool IsApplicationPaused => _pauseIndefinite || (_pauseUntilUtc is not null && _pauseUntilUtc > DateTimeOffset.UtcNow);
    private bool IsPausedEffective => IsApplicationPaused || _globalPauseActive() || !_definition.MonitoringEnabled;

    private async Task StartProcessLockedAsync(string reason, CancellationToken cancellationToken)
    {
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
            var process = _launcher.Launch(_definition);
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

    private async Task AttachProcessLockedAsync(Process process, string reason, CancellationToken cancellationToken)
    {
        DetachCurrentProcess();
        _process = process;
        _intentionalStop = false;

        try
        {
            _processStartedUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            _processStartedUtc = DateTimeOffset.UtcNow;
        }

        process.EnableRaisingEvents = true;
        process.Exited += ProcessOnExited;
        SetState(IsPausedEffective ? AppRuntimeState.Paused : AppRuntimeState.Healthy, reason, "Attached");

        await WriteEventAsync(AppLogLevel.Information, reason, reason, ProcessDiagnostics.Describe(process, _definition), cancellationToken).ConfigureAwait(false);
        StartHangMonitor(process);
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        _ = HandleProcessExitedAsync(sender as Process);
    }

    private async Task HandleProcessExitedAsync(Process? exitedProcess)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (exitedProcess is null || _process is null || exitedProcess.Id != _process.Id) return;

            var exitCode = SafeExitCode(exitedProcess);
            var uptime = _processStartedUtc is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - _processStartedUtc.Value;
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

            var permission = _restartLimiter.TryAcquire(DateTimeOffset.UtcNow);
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
                    _ = ResumeAfterBackoffAsync(permission.BackoffUntilUtc.Value, _lifetime.Token);
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

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _definition.RestartDelaySeconds)), _lifetime.Token).ConfigureAwait(false);
            if (!IsPausedEffective && !_shutdownActive())
            {
                await StartProcessLockedAsync("AutomaticRestart", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _gate.Release();
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
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                closeRequested = process.CloseMainWindow();
            }
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
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await WriteEventAsync(AppLogLevel.Warning, "ProcessForceKilled", "GracefulShutdownTimedOut", new { processId = process.Id }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteEventAsync(AppLogLevel.Error, "ProcessStopFailed", ex.GetType().Name, new { ex.Message }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void StartHangMonitor(Process process)
    {
        StopHangMonitor();
        if (!_definition.DetectHangs) return;

        _hangMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = HangMonitorLoopAsync(process, _hangMonitorCts.Token);
    }

    private async Task HangMonitorLoopAsync(Process process, CancellationToken cancellationToken)
    {
        DateTimeOffset? unresponsiveSince = null;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _definition.HangCheckIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_disposed || HasExited(process)) return;
                if (IsPausedEffective || _shutdownActive())
                {
                    unresponsiveSince = null;
                    continue;
                }
                if (_processStartedUtc is not null && DateTimeOffset.UtcNow - _processStartedUtc < TimeSpan.FromSeconds(Math.Max(0, _definition.StartupGraceSeconds)))
                {
                    continue;
                }

                IntPtr window;
                try
                {
                    process.Refresh();
                    window = process.MainWindowHandle;
                }
                catch
                {
                    continue;
                }

                if (window == IntPtr.Zero)
                {
                    unresponsiveSince = null;
                    continue;
                }

                var responsive = WindowResponsiveness.IsResponsive(window);
                if (responsive)
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
                    unresponsiveSince = DateTimeOffset.UtcNow;
                    await WriteEventAsync(AppLogLevel.Warning, "WindowUnresponsiveObserved", "WindowNotResponding", new
                    {
                        processId = process.Id,
                        timeoutSeconds = _definition.HangTimeoutSeconds
                    }, cancellationToken).ConfigureAwait(false);
                }
                SetState(AppRuntimeState.Unresponsive, "WindowNotResponding", "HangCheck");

                if (DateTimeOffset.UtcNow - unresponsiveSince.Value < TimeSpan.FromSeconds(Math.Max(1, _definition.HangTimeoutSeconds)))
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
                    process.Kill(entireProcessTree: false);
                }
                catch (Exception ex)
                {
                    await WriteEventAsync(AppLogLevel.Error, "HangRecoveryFailed", ex.GetType().Name, new { ex.Message }, CancellationToken.None).ConfigureAwait(false);
                }
                return;
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
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _pauseGeneration)) return;
            await ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ResumeAfterBackoffAsync(DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        try
        {
            var delay = untilUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || IsPausedEffective || _shutdownActive()) return;
                if (_process is null || HasExited(_process))
                {
                    SetState(AppRuntimeState.Restarting, "BackoffExpired", "AutomaticRecovery");
                    await StartProcessLockedAsync("BackoffExpired", cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
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

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        StopHangMonitor();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Deliberately detach only. Stopping AppWatcher must not kill managed applications.
            DetachCurrentProcess();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
