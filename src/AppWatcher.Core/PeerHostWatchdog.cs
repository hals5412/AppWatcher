namespace AppWatcher.Core;

// AgentとElevatedが互いの生存を確認し、異常終了した相手を登録済みタスクから起動し直す。
// 一度も応答を確認できていない相手（管理者Helperを使っていない環境など）は起動しない。
public sealed class PeerHostWatchdog : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    public const int MissesBeforeRestart = 2;
    public const int MaxRestartsPerHour = 3;

    private readonly PrivilegeLevel _peer;
    private readonly Func<bool> _suppressed;
    private readonly Func<CancellationToken, Task<bool>> _ping;
    private readonly Func<bool> _start;
    private readonly IEventSink _events;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<DateTimeOffset> _restarts = new();
    private Task _loop = Task.CompletedTask;
    private bool _seenOnline;
    private int _misses;
    private bool _limitLogged;

    public PeerHostWatchdog(
        PrivilegeLevel peer,
        Func<bool> suppressed,
        IEventSink events,
        Func<CancellationToken, Task<bool>>? ping = null,
        Func<bool>? start = null,
        TimeProvider? time = null,
        TimeSpan? interval = null)
    {
        _peer = peer;
        _suppressed = suppressed;
        _events = events;
        _ping = ping ?? (async token => (await new SupervisorClient(peer)
            .SendAsync(new SupervisorRequest(SupervisorCommandType.Ping), TimeSpan.FromSeconds(1), token)
            .ConfigureAwait(false)).Success);
        _start = start ?? (() => StartupTaskRunner.TryRun(peer));
        _time = time ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
    }

    public void Start() => _loop = RunAsync(_lifetime.Token);

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await CheckAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch { /* 監視処理の失敗でホストを止めない。 */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (_suppressed())
        {
            _misses = 0;
            return;
        }

        if (await _ping(token).ConfigureAwait(false))
        {
            _seenOnline = true;
            _misses = 0;
            return;
        }

        // 相手の終了と自分の終了がほぼ同時に起きる正常終了では、ここで起動し直さない。
        if (!_seenOnline || _suppressed() || ++_misses < MissesBeforeRestart) return;
        _misses = 0;

        var now = _time.GetUtcNow();
        while (_restarts.Count > 0 && now - _restarts.Peek() >= TimeSpan.FromHours(1)) _restarts.Dequeue();
        if (_restarts.Count >= MaxRestartsPerHour)
        {
            if (!_limitLogged)
            {
                _limitLogged = true;
                await WriteAsync(AppLogLevel.Error, "PeerHostRestartSuppressed", "RestartLimitExceeded", token).ConfigureAwait(false);
            }
            return;
        }

        _limitLogged = false;
        _restarts.Enqueue(now);
        var started = _start();
        await WriteAsync(
            started ? AppLogLevel.Warning : AppLogLevel.Error,
            started ? "PeerHostRestartRequested" : "PeerHostRestartFailed",
            "PeerHostUnresponsive",
            token).ConfigureAwait(false);
    }

    private Task WriteAsync(AppLogLevel level, string eventType, string reason, CancellationToken token) =>
        _events.WriteAsync(EventRecordFactory.Create(null, level, eventType, reason, new
        {
            peer = _peer.ToString(),
            taskName = StartupTaskRunner.TaskName(_peer)
        }), token);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
