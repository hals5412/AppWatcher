using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class HangAndRelaunchTests
{
    [Fact]
    public async Task LogOnlyHangActionRecordsOnceAndKeepsProcessRunning()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var events = new MemoryEvents();
        await using var supervisor = new ApplicationSupervisor(HangDefinition(HangAction.LogOnly), runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Responsive = false;

        await AdvanceUntilAsync(clock, () => events.Records.Any(e => e.EventType == "HangDetected"));
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.False(runtime.Processes[0].HasExited);
        Assert.Equal(1, runtime.Launches);
        Assert.Single(events.Records, e => e.EventType == "HangDetected");
        Assert.Equal(AppRuntimeState.Unresponsive, supervisor.Snapshot().State);

        runtime.Processes[0].Responsive = true;
        await AdvanceUntilAsync(clock, () => supervisor.Snapshot().State == AppRuntimeState.Healthy);
    }

    [Fact]
    public async Task RestartHangActionTerminatesUnresponsiveProcess()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var events = new MemoryEvents();
        await using var supervisor = new ApplicationSupervisor(HangDefinition(HangAction.Restart), runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Responsive = false;

        await AdvanceUntilAsync(clock, () => runtime.Processes[0].HasExited);
        await AdvanceUntilAsync(clock, () => runtime.Launches == 2);
        Assert.Contains(events.Records, e => e.EventType == "HangDetected");
    }

    [Fact]
    public async Task AutomaticRestartAttachesInstanceStartedDuringDelay()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var events = new MemoryEvents();
        var definition = TestWait.Definition(); definition.AttachExisting = true;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);

        // ユーザー操作やアプリ自身の再起動で、待機中に別インスタンスが起動した想定。
        runtime.External.Add(new FakeProcess(500, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(6));

        await TestWait.UntilAsync(() => supervisor.Snapshot().ProcessId == 500);
        Assert.Equal(1, runtime.Launches);
        Assert.Contains(events.Records, e => e.EventType == "ExistingProcessAttached");
    }

    [Fact]
    public async Task AutomaticRestartLaunchesWhenNoInstanceExists()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        var definition = TestWait.Definition(); definition.AttachExisting = true;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Equal(101, supervisor.Snapshot().ProcessId);
    }

    [Fact]
    public async Task DiscoveryFailureBeforeRestartStillLaunches()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var events = new MemoryEvents();
        var definition = TestWait.Definition(); definition.AttachExisting = true;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.FindExistingFails = true;
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Contains(events.Records, e => e.EventType == "ExistingProcessDiscoveryFailed");
    }

    [Fact]
    public async Task RestartWithoutAttachExistingIgnoresExternalInstance()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        runtime.External.Add(new FakeProcess(500, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Equal(101, supervisor.Snapshot().ProcessId);
    }

    [Fact]
    public void NewApplicationsDefaultToLogOnlyHangAction()
    {
        Assert.Equal(HangAction.LogOnly, new ApplicationDefinition().HangAction);
    }

    private static ApplicationDefinition HangDefinition(HangAction action)
    {
        var definition = TestWait.Definition();
        definition.DetectHangs = true;
        definition.HangCheckIntervalSeconds = 1;
        definition.HangTimeoutSeconds = 3;
        definition.StartupGraceSeconds = 0;
        definition.HangAction = action;
        return definition;
    }

    private static async Task AdvanceUntilAsync(TestClock clock, Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not reached while advancing the test clock.");
    }
}
