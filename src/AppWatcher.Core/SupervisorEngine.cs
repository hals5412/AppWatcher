using System.Collections.Concurrent;
using System.Reflection;

namespace AppWatcher.Core;

public sealed class SupervisorEngine : IAsyncDisposable
{
    private readonly PrivilegeLevel _hostPrivilege;
    private readonly ConfigService _configService;
    private readonly IEventSink _events;
    private readonly EventStore _eventStore;
    private readonly IProcessRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, ApplicationSupervisor> _supervisors = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _maintenanceGeneration;
    private readonly List<Task> _maintenanceTasks = [];
    private volatile bool _maintenanceActive;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private volatile bool _shuttingDown;
    private int _exitRequested;
    private DateTimeOffset? _maintenanceUntilUtc;
    private CancellationTokenSource? _maintenanceTimerCts;
    private AppWatcherConfiguration _configuration = new();
    private Task _logMaintenance = Task.CompletedTask;
    private Task _existingProcessDiscovery = Task.CompletedTask;
    private readonly bool _ownsEvents;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    public SupervisorEngine(PrivilegeLevel hostPrivilege, ConfigService configService, EventStore eventStore,
        IProcessRuntime? runtime = null, TimeProvider? time = null, IEventSink? events = null)
    {
        _hostPrivilege = hostPrivilege;
        _configService = configService;
        _eventStore = eventStore;
        _events = events ?? new ResilientEventSink(eventStore, time);
        _ownsEvents = events is null;
        _runtime = runtime ?? new WindowsProcessRuntime();
        _time = time ?? TimeProvider.System;
    }

    public PrivilegeLevel HostPrivilege => _hostPrivilege;
    public bool IsShuttingDown => _shuttingDown;
    public bool ExitRequested => Volatile.Read(ref _exitRequested) != 0;
    public bool IsMaintenanceActive => _maintenanceActive;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        _logMaintenance = Task.Run(MaintainLogsAsync);

        await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "HostStarted", "Startup", new
        {
            privilege = _hostPrivilege.ToString(),
            processId = Environment.ProcessId,
            startedUtc = _startedUtc
        }), cancellationToken).ConfigureAwait(false);

        _existingProcessDiscovery = DiscoverExistingProcessesAsync();
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var next = await _configService.LoadAsync(cancellationToken).ConfigureAwait(false);
            await ValidateConfigurationLockedAsync(next, cancellationToken).ConfigureAwait(false);
            var relevant = next.Applications
                .Where(a => a.Privilege == _hostPrivilege)
                .ToDictionary(a => a.Id);

            foreach (var current in _supervisors.ToArray())
            {
                if (!relevant.ContainsKey(current.Key))
                {
                    if (_supervisors.TryRemove(current.Key, out var removed))
                    {
                        await removed.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            foreach (var definition in relevant.Values)
            {
                if (_supervisors.TryGetValue(definition.Id, out var old))
                {
                    await old.UpdateAsync(definition, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var supervisor = new ApplicationSupervisor(
                    definition,
                    _runtime,
                    _events,
                    () => IsMaintenanceActive,
                    () => _shuttingDown,
                    _time);
                _supervisors[definition.Id] = supervisor;
                var previous = _configuration.Applications.FirstOrDefault(a => a.Id == definition.Id);
                if (previous is not null && previous.Privilege != definition.Privilege)
                    await supervisor.StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                else await supervisor.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            _configuration = next;

            await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "ConfigurationReloaded", "Reload", new
            {
                privilege = _hostPrivilege.ToString(),
                applications = relevant.Count
            }), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public HostSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        return new HostSnapshot(
            _hostPrivilege,
            currentProcess.Id,
            currentProcess.WorkingSet64,
            _startedUtc,
            now - _startedUtc,
            IsMaintenanceActive,
            _maintenanceUntilUtc,
            _supervisors.Values.Select(s => s.Snapshot()).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            version);
    }

    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        if (_hostPrivilege != PrivilegeLevel.Administrator)
        {
            throw new InvalidOperationException(
                "Running-process privilege inspection requires the elevated helper.");
        }

        return RunningProcessDiscovery.EnumerateCurrentSession();
    }

    public async Task StartApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var supervisor = GetSupervisor(id);
            await supervisor.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _reloadGate.Release(); }
    }

    public async Task StopApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var supervisor = GetSupervisor(id);
            await supervisor.StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _reloadGate.Release(); }
    }

    public async Task RestartApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var supervisor = GetSupervisor(id);
            await supervisor.RestartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _reloadGate.Release(); }
    }

    public async Task PauseApplicationAsync(Guid id, TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var supervisor = GetSupervisor(id);
            await supervisor.PauseAsync(duration, cancellationToken).ConfigureAwait(false);
        }
        finally { _reloadGate.Release(); }
    }

    public async Task ResumeApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var supervisor = GetSupervisor(id);
            await supervisor.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _reloadGate.Release(); }
    }

    public async Task SetMaintenanceAsync(TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();
            var generation = ++_maintenanceGeneration;
            _maintenanceActive = true;
            _maintenanceTimerCts?.Cancel();
            _maintenanceTimerCts?.Dispose();
            _maintenanceTimerCts = null;

            if (duration is null)
            {
                _maintenanceUntilUtc = null;
            }
            else
            {
                _maintenanceUntilUtc = _time.GetUtcNow().Add(duration.Value);
                _maintenanceTimerCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _maintenanceTasks.RemoveAll(t => t.IsCompleted);
                _maintenanceTasks.Add(ResumeMaintenanceAfterDelayAsync(duration.Value, generation, _maintenanceTimerCts.Token));
            }

            await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "MaintenanceStarted", "UserRequest", new
            {
                privilege = _hostPrivilege.ToString(),
                durationSeconds = duration?.TotalSeconds,
                untilUtc = _maintenanceUntilUtc
            }), cancellationToken).ConfigureAwait(false);

            foreach (var supervisor in _supervisors.Values)
            {
                await supervisor.OnGlobalPauseChangedAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _reloadGate.Release(); }
    }

    public async Task ResumeMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ResumeMaintenanceLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _reloadGate.Release(); }
    }

    private async Task ResumeMaintenanceLockedAsync(CancellationToken cancellationToken)
    {
        ThrowIfShuttingDown();
        ++_maintenanceGeneration;
        _maintenanceActive = false;
        _maintenanceUntilUtc = null;
        _maintenanceTimerCts?.Cancel();
        _maintenanceTimerCts?.Dispose();
        _maintenanceTimerCts = null;

        await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "MaintenanceEnded", "UserRequest", new
        {
            privilege = _hostPrivilege.ToString()
        }), cancellationToken).ConfigureAwait(false);

        foreach (var supervisor in _supervisors.Values)
        {
            await supervisor.OnGlobalPauseChangedAsync(false, cancellationToken).ConfigureAwait(false);
        }
    }

    public void BeginSystemShutdown()
    {
        _shuttingDown = true;
        _ = _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "SystemShutdownObserved", "SessionEnding", new
        {
            privilege = _hostPrivilege.ToString()
        }));
    }

    public async Task RequestHostShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return;

        // Suppress all automatic restart decisions before the host starts tearing down.
        // Disposing supervisors only detaches from target processes; it never terminates them.
        _shuttingDown = true;
        await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "HostShutdownRequested", "UserRequest", new
        {
            privilege = _hostPrivilege.ToString(),
            processId = Environment.ProcessId
        }), cancellationToken).ConfigureAwait(false);
    }

    private ApplicationSupervisor GetSupervisor(Guid id)
    {
        if (!_supervisors.TryGetValue(id, out var supervisor))
        {
            throw new KeyNotFoundException($"Application {id} is not managed by the {_hostPrivilege} host.");
        }
        return supervisor;
    }

    private async Task ResumeMaintenanceAfterDelayAsync(TimeSpan duration, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, _time, cancellationToken).ConfigureAwait(false);
            await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_shuttingDown && generation == _maintenanceGeneration)
                    await ResumeMaintenanceLockedAsync(_lifetime.Token).ConfigureAwait(false);
            }
            finally { _reloadGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shuttingDown)
            {
                // Already marked as shutting down, continue disposal.
            }
            _shuttingDown = true;
            _lifetime.Cancel();
            _maintenanceTimerCts?.Cancel();
            await Task.WhenAll(_maintenanceTasks).ConfigureAwait(false);
            await _existingProcessDiscovery.ConfigureAwait(false);
            await _logMaintenance.ConfigureAwait(false);

            foreach (var pair in _supervisors.ToArray())
            {
                if (_supervisors.TryRemove(pair.Key, out var supervisor))
                {
                    await supervisor.DisposeAsync().ConfigureAwait(false);
                }
            }

            await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "HostStopped", "Shutdown", new
            {
                privilege = _hostPrivilege.ToString(),
                uptimeSeconds = (DateTimeOffset.UtcNow - _startedUtc).TotalSeconds
            })).ConfigureAwait(false);

            _maintenanceTimerCts?.Dispose();
            if (_ownsEvents && _events is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
        finally { _reloadGate.Release(); }
    }

    private void ThrowIfShuttingDown()
    {
        if (_shuttingDown) throw new InvalidOperationException("Host is shutting down.");
    }

    public async Task ValidateConfigurationAsync(AppWatcherConfiguration configuration, CancellationToken token = default)
    {
        await _reloadGate.WaitAsync(token).ConfigureAwait(false);
        try { ThrowIfShuttingDown(); await ValidateConfigurationLockedAsync(configuration, token).ConfigureAwait(false); }
        finally { _reloadGate.Release(); }
    }

    private async Task ValidateConfigurationLockedAsync(AppWatcherConfiguration configuration, CancellationToken token)
    {
        if (configuration.Applications.Select(a => a.Id).Distinct().Count() != configuration.Applications.Count)
            throw new InvalidDataException("Duplicate application IDs are not allowed.");
        foreach (var definition in configuration.Applications)
        {
            var result = _configService.Validate(definition);
            if (!result.Success) throw new InvalidDataException(string.Join(Environment.NewLine, result.Messages));
            if (_supervisors.TryGetValue(definition.Id, out var supervisor))
                await supervisor.ValidateUpdateAsync(definition, token).ConfigureAwait(false);
        }
    }

    private async Task MaintainLogsAsync()
    {
        try
        {
            do
            {
                try { await _eventStore.MaintainAsync(_configuration.Global, _time.GetUtcNow(), _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    await new FallbackLog(Path.Combine(_eventStore.DataDirectory, "appwatcher-fallback.log"))
                        .WriteAsync($"LogMaintenanceFailed: {ex.GetType().Name}", _lifetime.Token).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromHours(1), _time, _lifetime.Token).ConfigureAwait(false);
            } while (!_lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task DiscoverExistingProcessesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_shuttingDown) return;

                foreach (var supervisor in _supervisors.Values.ToArray())
                {
                    if (_lifetime.IsCancellationRequested) return;
                    await supervisor.TryAttachExistingAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
