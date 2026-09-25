using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class TrayNotificationPlannerTests
{
    private static readonly Guid AppId = Guid.NewGuid();

    [Fact]
    public void FirstSnapshotProducesNoNotifications()
    {
        Assert.Empty(TrayNotificationPlanner.Diff(null, [Snapshot(AppRuntimeState.Backoff)]));
    }

    [Fact]
    public void AutomaticRestartIsReported()
    {
        var previous = Map(Snapshot(AppRuntimeState.Healthy, restarts: 1));
        var result = TrayNotificationPlanner.Diff(previous, [Snapshot(AppRuntimeState.Healthy, restarts: 2)]);
        Assert.Equal(TrayNotificationKind.Restarted, Assert.Single(result).Kind);
    }

    [Theory]
    [InlineData(AppRuntimeState.Backoff, TrayNotificationKind.Backoff)]
    [InlineData(AppRuntimeState.Unresponsive, TrayNotificationKind.Unresponsive)]
    [InlineData(AppRuntimeState.Failed, TrayNotificationKind.Failed)]
    public void EnteringProblemStateIsReported(AppRuntimeState state, TrayNotificationKind expected)
    {
        var result = TrayNotificationPlanner.Diff(Map(Snapshot(AppRuntimeState.Healthy)), [Snapshot(state)]);
        var notification = Assert.Single(result);
        Assert.Equal(expected, notification.Kind);
        Assert.Equal("Target", notification.ApplicationName);
    }

    [Fact]
    public void StayingInProblemStateIsNotReportedAgain()
    {
        Assert.Empty(TrayNotificationPlanner.Diff(Map(Snapshot(AppRuntimeState.Backoff)), [Snapshot(AppRuntimeState.Backoff)]));
    }

    [Fact]
    public void NormalTransitionsAndNewApplicationsAreQuiet()
    {
        Assert.Empty(TrayNotificationPlanner.Diff(Map(Snapshot(AppRuntimeState.Healthy)), [Snapshot(AppRuntimeState.Stopped), Snapshot(AppRuntimeState.Failed, id: Guid.NewGuid())]));
    }

    [Fact]
    public void RestartCountFromNewHostDoesNotReport()
    {
        // ホスト再起動で累計が0に戻っても通知しない。
        Assert.Empty(TrayNotificationPlanner.Diff(Map(Snapshot(AppRuntimeState.Healthy, restarts: 5)), [Snapshot(AppRuntimeState.Healthy, restarts: 0)]));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(null, false, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void HelperOfflineIsReportedOnlyOnTransition(bool? wasOnline, bool isOnline, bool required, bool expected)
    {
        Assert.Equal(expected, TrayNotificationPlanner.HelperWentOffline(wasOnline, isOnline, required));
    }

    private static Dictionary<Guid, ApplicationSnapshot> Map(ApplicationSnapshot snapshot) => new() { [snapshot.Id] = snapshot };

    private static ApplicationSnapshot Snapshot(AppRuntimeState state, int restarts = 0, Guid? id = null) => new(
        id ?? AppId, "Target", PrivilegeLevel.Normal, state, 100, null, null, 0, null, false,
        "Event", null, @"C:\Apps\Target.exe", true, false, restarts);
}
