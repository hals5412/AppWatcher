namespace AppWatcher.Core;

public enum TrayNotificationKind
{
    Restarted,
    Backoff,
    Unresponsive,
    Failed,
    HelperOffline
}

public sealed record TrayNotification(TrayNotificationKind Kind, string? ApplicationName = null);

// 前回と今回のSnapshotを比べ、利用者に知らせるべき変化だけを取り出す。
// 初回（比較元なし）や新しく現れたアプリは通知しない。
public static class TrayNotificationPlanner
{
    public static IReadOnlyList<TrayNotification> Diff(
        IReadOnlyDictionary<Guid, ApplicationSnapshot>? previous,
        IEnumerable<ApplicationSnapshot> current)
    {
        if (previous is null) return [];

        var notifications = new List<TrayNotification>();
        foreach (var app in current)
        {
            if (!previous.TryGetValue(app.Id, out var before)) continue;

            if (app.AutomaticRestartCount > before.AutomaticRestartCount)
            {
                notifications.Add(new TrayNotification(TrayNotificationKind.Restarted, app.Name));
            }

            if (app.State == before.State) continue;
            var kind = app.State switch
            {
                AppRuntimeState.Backoff => TrayNotificationKind.Backoff,
                AppRuntimeState.Unresponsive => TrayNotificationKind.Unresponsive,
                AppRuntimeState.Failed => TrayNotificationKind.Failed,
                _ => (TrayNotificationKind?)null
            };
            if (kind is not null) notifications.Add(new TrayNotification(kind.Value, app.Name));
        }
        return notifications;
    }

    public static bool HelperWentOffline(bool? wasOnline, bool isOnline, bool helperRequired) =>
        helperRequired && wasOnline == true && !isOnline;
}
