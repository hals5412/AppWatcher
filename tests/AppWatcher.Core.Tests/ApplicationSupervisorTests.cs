using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class ApplicationSupervisorTests
{
    [Fact]
    public async Task BackoffAndRestartHistorySurviveUpdatesAndStopCancelsRecovery()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        definition.MaxRestarts = 1;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        runtime.Processes[1].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Backoff && clock.ActiveTimers > 0);
        var changed = supervisor.Definition; changed.Name = "Updated while backing off";
        await supervisor.UpdateAsync(changed, TestContext.Current.CancellationToken);
        Assert.Equal(AppRuntimeState.Backoff, supervisor.Snapshot().State);
        Assert.Equal(1, supervisor.Snapshot().RestartCountInWindow);
        await supervisor.PauseAsync(null, TestContext.Current.CancellationToken);
        await supervisor.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppRuntimeState.Backoff, supervisor.Snapshot().State);
        await supervisor.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, runtime.Launches);
    }

    [Fact]
    public async Task BackoffExpiryReevaluatesPolicyBeforeLaunching()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        definition.MaxRestarts = 1;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        runtime.Processes[1].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Backoff && clock.ActiveTimers > 0);
        var changed = supervisor.Definition; changed.RestartPolicy = RestartPolicy.Never;
        await supervisor.UpdateAsync(changed, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        await TestWait.UntilAsync(() => supervisor.Snapshot().LastReason == "RestartPolicyDeclined");
        Assert.Equal(2, runtime.Launches);
    }

    [Fact]
    public async Task RestartIgnoresOldProcessExitCallbackAndLeavesExactlyOneTarget()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.RestartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Single(runtime.Processes, p => !p.HasExited);
        await supervisor.DisposeAsync();
        Assert.False(runtime.Processes[1].HasExited);
    }

    [Fact]
    public async Task ManualStopSurvivesPauseResumeAndDefinitionUpdate()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.PauseAsync(null, TestContext.Current.CancellationToken);
        await supervisor.ResumeAsync(TestContext.Current.CancellationToken);
        await supervisor.OnGlobalPauseChangedAsync(true, TestContext.Current.CancellationToken);
        await supervisor.OnGlobalPauseChangedAsync(false, TestContext.Current.CancellationToken);
        var changed = supervisor.Definition;
        changed.Name = "Renamed";
        await supervisor.UpdateAsync(changed, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Null(supervisor.Snapshot().ProcessId);
    }

    [Fact]
    public async Task StopCancelsScheduledAutomaticRestart()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().State == AppRuntimeState.Restarting && clock.ActiveTimers > 0);
        await supervisor.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
    }

    [Fact]
    public async Task PauseExitRespectsNeverRestartPolicy()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        definition.RestartPolicy = RestartPolicy.Never;
        var events = new MemoryEvents();
        await using var supervisor = new ApplicationSupervisor(definition, runtime, events, () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.PauseAsync(null, TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => events.Records.Any(e => e.EventType == "ProcessExited"));
        await supervisor.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Equal(AppRuntimeState.Stopped, supervisor.Snapshot().State);
    }

    [Fact]
    public async Task FailedStopKeepsPidAndDoesNotReportStopped()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        definition.ForceKillAfterTimeout = false;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StopAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(supervisor.Snapshot().ProcessId);
        Assert.Equal(AppRuntimeState.Failed, supervisor.Snapshot().State);
    }

    [Fact]
    public async Task UpdateRetainsRunningProcessWithoutAttachExisting()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var definition = TestWait.Definition();
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        var next = TestWait.Definition(); next.Id = definition.Id; next.Name = "Updated";
        await supervisor.UpdateAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Equal(100, supervisor.Snapshot().ProcessId);
        next = TestWait.Definition(); next.Id = definition.Id; next.Privilege = PrivilegeLevel.Administrator;
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.UpdateAsync(next, TestContext.Current.CancellationToken));
        Assert.Equal(100, supervisor.Snapshot().ProcessId);
    }

    [Fact]
    public async Task NewPauseSupersedesOlderTimer()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.PauseAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        await supervisor.PauseAsync(null, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(supervisor.Snapshot().PauseIndefinite);
    }
}
