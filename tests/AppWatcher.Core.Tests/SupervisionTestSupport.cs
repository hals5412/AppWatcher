using System.Collections.Concurrent;
using AppWatcher.Core;

namespace AppWatcher.Core.Tests;

internal sealed class TestClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public int ActiveTimers { get { lock (_sync) return _timers.Count(t => t.Due is not null); } }
    public void Advance(TimeSpan duration)
    {
        List<Timer> due;
        lock (_sync)
        {
            _now += duration;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class Timer(TestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime; Period = period; return true; }
        }
        public void Dispose() { lock (owner._sync) { Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class MemoryEvents : IEventSink
{
    public ConcurrentQueue<EventRecord> Records { get; } = new();
    public Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default) { Records.Enqueue(record); return Task.CompletedTask; }
}

internal sealed class FakeRuntime(TestClock clock) : IProcessRuntime
{
    public List<FakeProcess> Processes { get; } = [];
    public int Launches => Processes.Count;
    public IManagedProcess? FindExisting(ApplicationDefinition definition) => Processes.LastOrDefault(p => !p.HasExited);
    public IManagedProcess Launch(ApplicationDefinition definition)
    {
        var process = new FakeProcess(100 + Launches, clock.GetUtcNow());
        Processes.Add(process);
        return process;
    }
}

internal sealed class FakeProcess(int id, DateTimeOffset started) : IManagedProcess
{
    public int Id => id;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public DateTimeOffset StartedUtc => started;
    public bool KillFails { get; set; }
    public event EventHandler? Exited;
    public void ObserveExit() { if (HasExited) Exited?.Invoke(this, EventArgs.Empty); }
    public bool? IsWindowResponsive() => true;
    public bool CloseMainWindow() => false;
    public void Kill() { if (!KillFails) Exit(0); }
    public void Exit(int code) { HasExited = true; ExitCode = code; Exited?.Invoke(this, EventArgs.Empty); }
    public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }
}

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    public static ApplicationDefinition Definition() => new()
    {
        Name = "Isolated test",
        ExecutablePath = Environment.ProcessPath!,
        DetectHangs = false,
        AttachExisting = false,
        RestartDelaySeconds = 5
    };
}
