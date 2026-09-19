using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

internal sealed class AuditDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppWatcher-Audit-" + Guid.NewGuid().ToString("N"));
    public AuditDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public sealed class SupervisorEngineTests
{
    [Fact]
    public async Task PrivilegeTransferRegistersStoppedAtDestination()
    {
        using var directory = new AuditDirectory(); var config = new ConfigService(directory.Path); var definition = TestWait.Definition();
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [definition] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var normalRuntime = new FakeRuntime(clock); var adminRuntime = new FakeRuntime(clock);
        await using var normal = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), normalRuntime, clock, new MemoryEvents());
        await using var admin = new SupervisorEngine(PrivilegeLevel.Administrator, config, new EventStore(directory.Path), adminRuntime, clock, new MemoryEvents());
        await normal.ReloadAsync(TestContext.Current.CancellationToken); await admin.ReloadAsync(TestContext.Current.CancellationToken);
        await normal.StopApplicationAsync(definition.Id, TestContext.Current.CancellationToken);
        await config.UpdateAsync(c => c.Applications[0].Privilege = PrivilegeLevel.Administrator, TestContext.Current.CancellationToken);
        await normal.ReloadAsync(TestContext.Current.CancellationToken); await admin.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(normal.Snapshot().Applications);
        Assert.Equal(AppRuntimeState.Stopped, Assert.Single(admin.Snapshot().Applications).State);
        Assert.Equal(0, adminRuntime.Launches);
        await admin.StartApplicationAsync(definition.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, adminRuntime.Launches);
    }

    [Fact]
    public async Task TimedMaintenanceResumesWithoutCancellingItself()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path); var definition = TestWait.Definition();
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [definition] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), runtime, clock, new MemoryEvents());
        await engine.ReloadAsync(TestContext.Current.CancellationToken);
        await engine.SetMaintenanceAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.True(engine.IsMaintenanceActive);
        clock.Advance(TimeSpan.FromMinutes(2));
        await TestWait.UntilAsync(() => !engine.IsMaintenanceActive && engine.Snapshot().Applications[0].State == AppRuntimeState.Healthy);
        Assert.Equal(1, runtime.Launches);
    }

    [Fact]
    public async Task ReloadDoesNotRestartStoppedOrUnchangedApplications()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path); var stopped = TestWait.Definition(); var running = TestWait.Definition();
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [stopped, running] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), runtime, clock, new MemoryEvents());
        await engine.ReloadAsync(TestContext.Current.CancellationToken);
        await engine.StopApplicationAsync(stopped.Id, TestContext.Current.CancellationToken);
        await engine.PauseApplicationAsync(running.Id, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        await config.UpdateAsync(c => c.Applications.Add(TestWait.Definition()), TestContext.Current.CancellationToken);
        await engine.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, runtime.Launches);
        Assert.Null(engine.Snapshot().Applications.Single(a => a.Id == stopped.Id).ProcessId);
        Assert.Equal(AppRuntimeState.Paused, engine.Snapshot().Applications.Single(a => a.Id == running.Id).State);
    }

    [Fact]
    public async Task DuplicateIdsAreRejectedBeforeExistingMonitoringChanges()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path); var definition = TestWait.Definition();
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [definition] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), runtime, clock, new MemoryEvents());
        await engine.ReloadAsync(TestContext.Current.CancellationToken);
        await config.UpdateAsync(c => c.Applications.Add(c.Applications[0]), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ReloadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, runtime.Launches);
        Assert.NotNull(Assert.Single(engine.Snapshot().Applications).ProcessId);
    }

    [Fact]
    public async Task NewMaintenanceSupersedesOldTimer()
    {
        using var directory = new AuditDirectory(); var clock = new TestClock();
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, new ConfigService(directory.Path), new EventStore(directory.Path), new FakeRuntime(clock), clock, new MemoryEvents());
        await engine.SetMaintenanceAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        await engine.SetMaintenanceAsync(null, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(engine.IsMaintenanceActive);
        await engine.ResumeMaintenanceAsync(TestContext.Current.CancellationToken);
        Assert.False(engine.IsMaintenanceActive);
    }
}
