using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class RestartHistoryTests
{
    [Fact]
    public async Task CountsAutomaticRestartsPerApplicationForRecentAndTotal()
    {
        using var directory = new AuditDirectory();
        var store = new EventStore(directory.Path);
        var app = new ApplicationDefinition { Name = "Target" };
        var other = new ApplicationDefinition { Name = "Other" };
        var now = DateTimeOffset.UtcNow;

        await store.WriteAsync(Restart(app, now.AddDays(-10)), TestContext.Current.CancellationToken);
        await store.WriteAsync(Restart(app, now.AddHours(-30)), TestContext.Current.CancellationToken);
        await store.WriteAsync(Restart(app, now.AddHours(-2)), TestContext.Current.CancellationToken);
        await store.WriteAsync(Restart(other, now.AddMinutes(-5)), TestContext.Current.CancellationToken);
        // 手動の再起動や起動時の開始は数えない。
        await store.WriteAsync(EventRecordFactory.Create(app, AppLogLevel.Information, AutomaticRestartEvent.EventType, "ManualRestart") with { TimestampUtc = now }, TestContext.Current.CancellationToken);
        await store.WriteAsync(EventRecordFactory.Create(app, AppLogLevel.Information, AutomaticRestartEvent.EventType, "WatcherStartup") with { TimestampUtc = now }, TestContext.Current.CancellationToken);

        var counts = await store.GetAutomaticRestartCountsAsync(now.AddHours(-24), TestContext.Current.CancellationToken);

        Assert.Equal(new RestartCounts(1, 3), counts[app.Id]);
        Assert.Equal(new RestartCounts(1, 1), counts[other.Id]);
    }

    [Fact]
    public async Task ApplicationsWithoutRestartsAreAbsent()
    {
        using var directory = new AuditDirectory();
        var counts = await new EventStore(directory.Path).GetAutomaticRestartCountsAsync(DateTimeOffset.UtcNow.AddHours(-24), TestContext.Current.CancellationToken);
        Assert.Empty(counts);
    }

    [Fact]
    public async Task AutomaticRestartIsRecordedEvenWhenLogLevelIsHigher()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var events = new MemoryEvents();
        var definition = TestWait.Definition();
        definition.LogLevel = AppLogLevel.Error;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);

        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        await TestWait.UntilAsync(() => events.Records.Any(e => e.ReasonCode == AutomaticRestartEvent.ReasonCode));

        // 情報レベルの他のイベント（起動時の開始など）はログレベルどおり記録しない。
        Assert.Single(events.Records, e => e.EventType == AutomaticRestartEvent.EventType);
        Assert.DoesNotContain(events.Records, e => e.EventType == "ProcessStarted");
    }

    private static EventRecord Restart(ApplicationDefinition app, DateTimeOffset at) =>
        EventRecordFactory.Create(app, AppLogLevel.Information, AutomaticRestartEvent.EventType, AutomaticRestartEvent.ReasonCode) with { TimestampUtc = at };
}
