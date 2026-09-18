namespace AppWatcher.Core;

public readonly record struct RestartPermission(bool Allowed, bool EnteredBackoff, DateTimeOffset? BackoffUntilUtc, string ReasonCode);

public sealed class RestartLimiter(ApplicationDefinition definition)
{
    private readonly Queue<DateTimeOffset> _restartAttempts = new();
    private DateTimeOffset? _backoffUntilUtc;

    public int AttemptsInWindow
    {
        get
        {
            Prune(DateTimeOffset.UtcNow);
            return _restartAttempts.Count;
        }
    }

    public DateTimeOffset? BackoffUntilUtc => _backoffUntilUtc;

    public RestartPermission TryAcquire(DateTimeOffset now)
    {
        if (!definition.RestartLoopProtectionEnabled)
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
        if (_restartAttempts.Count >= definition.MaxRestarts)
        {
            _backoffUntilUtc = now.AddMinutes(Math.Max(1, definition.BackoffMinutes));
            return new RestartPermission(false, true, _backoffUntilUtc, "RestartLimitExceeded");
        }

        _restartAttempts.Enqueue(now);
        return new RestartPermission(true, false, null, "RestartAllowed");
    }

    public void ResetHealthy()
    {
        _restartAttempts.Clear();
        _backoffUntilUtc = null;
    }

    private void Prune(DateTimeOffset now)
    {
        var threshold = now.AddMinutes(-Math.Max(1, definition.RestartWindowMinutes));
        while (_restartAttempts.Count > 0 && _restartAttempts.Peek() < threshold)
        {
            _restartAttempts.Dequeue();
        }
    }
}
