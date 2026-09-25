using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class PeerHostWatchdogTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public TestClock Clock { get; } = new();
        public MemoryEvents Events { get; } = new();
        public bool Online { get; set; }
        public bool Suppressed { get; set; }
        public bool StartSucceeds { get; set; } = true;
        public int Pings;
        public int Starts;
        public PeerHostWatchdog Watchdog { get; }

        public Harness()
        {
            Watchdog = new PeerHostWatchdog(
                PrivilegeLevel.Administrator,
                () => Suppressed,
                Events,
                _ => { Interlocked.Increment(ref Pings); return Task.FromResult(Online); },
                () => { Interlocked.Increment(ref Starts); return StartSucceeds; },
                Clock,
                TimeSpan.FromSeconds(30));
            Watchdog.Start();
        }

        // 1回分の確認が終わるまで待つ（抑止中はpingしないため、その場合はイベントを待たない）。
        public async Task TickAsync()
        {
            await TestWait.UntilAsync(() => Clock.ActiveTimers > 0);
            var before = Pings;
            Clock.Advance(TimeSpan.FromSeconds(30));
            if (Suppressed) { await Task.Delay(20); return; }
            await TestWait.UntilAsync(() => Pings > before);
            await Task.Delay(20);
        }

        public ValueTask DisposeAsync() => Watchdog.DisposeAsync();
    }

    [Fact]
    public async Task NeverStartsPeerThatWasNeverOnline()
    {
        await using var h = new Harness();
        for (var i = 0; i < 5; i++) await h.TickAsync();
        Assert.Equal(0, h.Starts);
    }

    [Fact]
    public async Task RestartsPeerAfterTwoConsecutiveMisses()
    {
        await using var h = new Harness { Online = true };
        await h.TickAsync();
        h.Online = false;
        await h.TickAsync();
        Assert.Equal(0, h.Starts);
        await h.TickAsync();
        Assert.Equal(1, h.Starts);
        Assert.Contains(h.Events.Records, e => e.EventType == "PeerHostRestartRequested");
    }

    [Fact]
    public async Task DoesNotRestartWhileOwnHostIsShuttingDown()
    {
        await using var h = new Harness { Online = true };
        await h.TickAsync();
        h.Online = false;
        h.Suppressed = true;
        for (var i = 0; i < 4; i++) await h.TickAsync();
        Assert.Equal(0, h.Starts);
    }

    [Fact]
    public async Task LimitsRestartsPerHour()
    {
        await using var h = new Harness { Online = true };
        await h.TickAsync();
        h.Online = false;
        // 2回の未応答ごとに1回起動する。上限3回を超えた分は起動しない。
        for (var i = 0; i < 10; i++) await h.TickAsync();
        Assert.Equal(PeerHostWatchdog.MaxRestartsPerHour, h.Starts);
        Assert.Single(h.Events.Records, e => e.EventType == "PeerHostRestartSuppressed");
    }

    [Fact]
    public async Task RecordsFailedStart()
    {
        await using var h = new Harness { Online = true, StartSucceeds = false };
        await h.TickAsync();
        h.Online = false;
        await h.TickAsync();
        await h.TickAsync();
        Assert.Contains(h.Events.Records, e => e.EventType == "PeerHostRestartFailed");
    }
}
