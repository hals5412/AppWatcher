using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class ResourceUsageTests
{
    [Fact]
    public async Task DiscoverySharesOneScopePerTickAndSkipsWhenNothingIsPending()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path);
        var first = TestWait.Definition(); first.AttachExisting = true; first.StartWithWatcher = false;
        var second = TestWait.Definition(); second.AttachExisting = true; second.StartWithWatcher = false;
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [first, second] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), runtime, clock, new MemoryEvents());
        await engine.StartAsync(TestContext.Current.CancellationToken);

        runtime.External.Add(new FakeProcess(500, clock.GetUtcNow()));
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(5));
        await TestWait.UntilAsync(() => engine.Snapshot().Applications.All(a => a.ProcessId == 500));
        Assert.Equal(1, runtime.ScopesCreated);

        // 全対象が監視中なら、次の周期ではプロセス一覧を取得しない。
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.ScopesCreated);
        Assert.Equal(0, runtime.Launches);
    }

    [Fact]
    public async Task AutomaticRestartCountIncreasesOnlyForAutomaticRestarts()
    {
        var clock = new TestClock(); var runtime = new FakeRuntime(clock);
        await using var supervisor = new ApplicationSupervisor(TestWait.Definition(), runtime, new MemoryEvents(), () => false, () => false, clock);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);

        await supervisor.RestartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(6));
        await TestWait.UntilAsync(() => runtime.Launches == 2);
        Assert.Equal(0, supervisor.Snapshot().AutomaticRestartCount);

        runtime.Processes[1].Exit(1);
        await TestWait.UntilAsync(() => supervisor.Snapshot().AutomaticRestartCount == 1);
    }

    [Fact]
    public async Task ExitRequestedTaskCompletesOnHostShutdownRequest()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, new EventStore(directory.Path), new FakeRuntime(new TestClock()), new TestClock(), new MemoryEvents());
        Assert.False(engine.ExitRequestedTask.IsCompleted);
        await engine.RequestHostShutdownAsync(TestContext.Current.CancellationToken);
        await engine.ExitRequestedTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(engine.ExitRequested);
    }

    [Fact]
    public async Task ConfigFileStampChangesOnlyWhenConfigurationIsSaved()
    {
        using var directory = new AuditDirectory();
        var config = new ConfigService(directory.Path);
        await config.LoadAsync(TestContext.Current.CancellationToken);
        var before = config.GetFileStamp();
        Assert.NotEqual(default, before);

        await config.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, config.GetFileStamp());

        await config.UpdateAsync(c => c.Global.UiRefreshSeconds = 9, TestContext.Current.CancellationToken);
        Assert.NotEqual(before, config.GetFileStamp());
    }

    [Fact]
    public async Task LogMaintenanceSkipsQuietlyWhileAnotherHostHoldsTheLock()
    {
        using var directory = new AuditDirectory();
        var store = new EventStore(directory.Path);
        await using var otherHost = new FileStream(Path.Combine(directory.Path, "events.maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await store.MaintainAsync(new GlobalSettings(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(directory.Path, "events.maintenance.timestamp")));
    }

    [Fact]
    public void RemovedUpdateCheckSettingIsIgnoredWhenLoading()
    {
        var json = """{ "global": { "checkForUpdates": true, "showNotifications": false } }""";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppWatcherConfiguration>(json, JsonDefaults.Options)!;
        Assert.False(loaded.Global.ShowNotifications);
        Assert.True(new GlobalSettings().ShowNotifications);
    }
}
