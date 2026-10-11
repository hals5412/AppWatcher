using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class DisabledMonitoringTests
{
    [Fact]
    public async Task DisabledApplicationIsReportedSeparatelyAndNotRestarted()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);

        await supervisor.UpdateAsync(Changed(supervisor, enabled: false), TestContext.Current.CancellationToken);
        var snapshot = supervisor.Snapshot();
        Assert.Equal(AppRuntimeState.Disabled, snapshot.State);
        Assert.Equal("MonitoringDisabled", snapshot.LastReason);
        // 無効にしてもアプリ自体は終了させない。
        Assert.False(runtime.Processes[0].HasExited);

        runtime.Processes[0].Exit(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Equal(AppRuntimeState.Disabled, supervisor.Snapshot().State);
    }

    [Fact]
    public async Task DisabledApplicationIsNotLaunchedOrDiscoveredAtStartup()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        var definition = TestWait.Definition();
        definition.MonitoringEnabled = false;
        definition.AttachExisting = true;
        await using var supervisor = new ApplicationSupervisor(definition, runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.Launches);
        // 恒久的に無効なアプリのために、定期的なプロセス探索を続けない。
        Assert.False(supervisor.NeedsExistingDiscovery);
        runtime.External.Add(new FakeProcess(500, clock.GetUtcNow()));
        Assert.False(await supervisor.TryAttachExistingAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReenablingRestartsApplicationThatExitedWhileDisabled()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.UpdateAsync(Changed(supervisor, enabled: false), TestContext.Current.CancellationToken);
        runtime.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().ProcessId is null);

        await supervisor.UpdateAsync(Changed(supervisor, enabled: true), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Equal(AppRuntimeState.Healthy, supervisor.Snapshot().State);
    }

    [Fact]
    public async Task ReenablingKeepsManualStop()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.UpdateAsync(Changed(supervisor, enabled: false), TestContext.Current.CancellationToken);
        await supervisor.UpdateAsync(Changed(supervisor, enabled: true), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Equal(AppRuntimeState.Stopped, supervisor.Snapshot().State);
    }

    [Fact]
    public void EnteringDisabledStateDoesNotNotify()
    {
        var before = Snapshot(AppRuntimeState.Healthy);
        Assert.Empty(TrayNotificationPlanner.Diff(new Dictionary<Guid, ApplicationSnapshot> { [before.Id] = before }, [before with { State = AppRuntimeState.Disabled }]));
    }

    private static ApplicationDefinition Changed(ApplicationSupervisor supervisor, bool enabled)
    {
        var definition = supervisor.Definition;
        definition.MonitoringEnabled = enabled;
        return definition;
    }

    private static ApplicationSnapshot Snapshot(AppRuntimeState state) => new(
        Guid.NewGuid(), "Target", PrivilegeLevel.Normal, state, 100, null, null, 0, null, false,
        "Event", null, @"C:\Apps\Target.exe", true, false);
}
