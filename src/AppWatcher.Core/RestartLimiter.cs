namespace AppWatcher.Core;

public readonly record struct RestartPermission(bool Allowed, bool EnteredBackoff, DateTimeOffset? BackoffUntilUtc, string ReasonCode);

public sealed class RestartLimiter(ApplicationDefinition definition)
{
    private ApplicationDefinition _definition = definition;
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _restartAttempts = new();
    private DateTimeOffset? _backoffUntilUtc;

    public int AttemptsInWindow
    {
        get
        {
            return CountAt(DateTimeOffset.UtcNow);
        }
    }

    public DateTimeOffset? BackoffUntilUtc { get { lock (_sync) return _backoffUntilUtc; } }

    public int CountAt(DateTimeOffset now) { lock (_sync) return _restartAttempts.Count(t => t >= now.AddMinutes(-Math.Max(1, _definition.RestartWindowMinutes))); }
    public void UpdateDefinition(ApplicationDefinition value) { lock (_sync) _definition = value; }

    public RestartPermission TryAcquire(DateTimeOffset now)
    {
        lock (_sync) return TryAcquireLocked(now);
    }

    private RestartPermission TryAcquireLocked(DateTimeOffset now)
    {
        Prune(now);
        if (!_definition.RestartLoopProtectionEnabled)
        {
            _restartAttempts.Enqueue(now);
            return new RestartPermission(true, false, null, "LoopProtectionDisabled");
        }

        if (_backoffUntilUtc is not null)
        {
            if (now < _backoffUntilUtc.Value)
            {
                return new RestartPermission(false, false, _backoffUntilUtc, "BackoffActive");
            }
            _backoffUntilUtc = null;
        }

        Prune(now);
        if (_restartAttempts.Count >= _definition.MaxRestarts)
        {
            _backoffUntilUtc = now.AddMinutes(Math.Max(1, _definition.BackoffMinutes));
            return new RestartPermission(false, true, _backoffUntilUtc, "RestartLimitExceeded");
        }

        _restartAttempts.Enqueue(now);
        return new RestartPermission(true, false, null, "RestartAllowed");
    }

    public void ResetHealthy()
    {
        lock (_sync)
        {
            _restartAttempts.Clear();
            _backoffUntilUtc = null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var threshold = now.AddMinutes(-Math.Max(1, _definition.RestartWindowMinutes));
        while (_restartAttempts.Count > 0 && _restartAttempts.Peek() < threshold)
        {
            _restartAttempts.Dequeue();
        }
    }
}
