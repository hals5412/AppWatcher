using System.Collections.Concurrent;
using System.Reflection;

namespace AppWatcher.Core;

public sealed class SupervisorEngine : IAsyncDisposable
{
    private readonly PrivilegeLevel _hostPrivilege;
    private readonly ConfigService _configService;
    private readonly IEventSink _events;
    private readonly EventStore _eventStore;
    private readonly ProcessMatcher _matcher = new();
    private readonly InteractiveProcessLauncher _launcher = new();
    private readonly ConcurrentDictionary<Guid, ApplicationSupervisor> _supervisors = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private volatile bool _shuttingDown;
    private int _exitRequested;
    private volatile bool _maintenanceIndefinite;
    private DateTimeOffset? _maintenanceUntilUtc;
    private CancellationTokenSource? _maintenanceTimerCts;
    private AppWatcherConfiguration _configuration = new();

    public SupervisorEngine(PrivilegeLevel hostPrivilege, ConfigService configService, EventStore eventStore)
    {
        _hostPrivilege = hostPrivilege;
        _configService = configService;
        _eventStore = eventStore;
        _events = new ResilientEventSink(eventStore);
    }

    public PrivilegeLevel HostPrivilege => _hostPrivilege;
    public bool IsShuttingDown => _shuttingDown;
    public bool ExitRequested => Volatile.Read(ref _exitRequested) != 0;
    public bool IsMaintenanceActive => _maintenanceIndefinite || (_maintenanceUntilUtc is not null && _maintenanceUntilUtc > DateTimeOffset.UtcNow);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _eventStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var retention = Math.Max(1, _configuration.Global.EventRetentionDays);
            await _eventStore.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-retention), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Retention maintenance is non-critical.
        }

        await _events.WriteAsync(EventRecordFactory.Create(null, AppLogLevel.Information, "HostStarted", "Startup", new
        {
            privilege = _hostPrivilege.ToString(),
            processId = Environment.ProcessId,
            startedUtc = _startedUtc
        }), cancellationToken).ConfigureAwait(false);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _configuration = await _configService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var relevant = _configuration.Applications
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
                if (_supervisors.TryRemove(definition.Id, out var old))
                {
                    await old.DisposeAsync().ConfigureAwait(false);
                }

                var supervisor = new ApplicationSupervisor(
                    definition,
                    _matcher,
                    _launcher,
                    _events,
                    () => IsMaintenanceActive,
                    () => _shuttingDown);
                _supervisors[definition.Id] = supervisor;
                await supervisor.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

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
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
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

    public async Task StartApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supervisor = GetSupervisor(id);
        await supervisor.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task StopApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supervisor = GetSupervisor(id);
        await supervisor.StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supervisor = GetSupervisor(id);
        await supervisor.RestartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseApplicationAsync(Guid id, TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        var supervisor = GetSupervisor(id);
        await supervisor.PauseAsync(duration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supervisor = GetSupervisor(id);
        await supervisor.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMaintenanceAsync(TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        _maintenanceTimerCts?.Cancel();
        _maintenanceTimerCts?.Dispose();
        _maintenanceTimerCts = null;

        if (duration is null)
        {
            _maintenanceIndefinite = true;
            _maintenanceUntilUtc = null;
        }
        else
        {
            _maintenanceIndefinite = false;
            _maintenanceUntilUtc = DateTimeOffset.UtcNow.Add(duration.Value);
            _maintenanceTimerCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = ResumeMaintenanceAfterDelayAsync(duration.Value, _maintenanceTimerCts.Token);
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

    public async Task ResumeMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        _maintenanceIndefinite = false;
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

    private async Task ResumeMaintenanceAfterDelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            await ResumeMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shuttingDown)
        {
            // Already marked as shutting down, continue disposal.
        }
        _shuttingDown = true;
        _lifetime.Cancel();
        _maintenanceTimerCts?.Cancel();

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
        _lifetime.Dispose();
        _reloadGate.Dispose();
    }
}
