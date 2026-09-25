using System.Diagnostics;
using AppWatcher.Core;
using Microsoft.Win32;

namespace AppWatcher.Agent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SupervisorEngine _engine;
    private readonly SupervisorClient _adminClient = new(PrivilegeLevel.Administrator);
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly WindowsFormsSynchronizationContext _uiContext = new();
    private readonly ConfigService _configService = new();
    private AppWatcherConfiguration? _config;
    private (DateTime LastWriteUtc, long Length) _configStamp;
    private Dictionary<Guid, ApplicationSnapshot>? _previousApps;
    private bool? _helperWasOnline;
    private bool _updating;
    private bool _exiting;

    public TrayApplicationContext(SupervisorEngine engine)
    {
        _engine = engine;
        _appIcon = (Icon)(Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application).Clone();

        var menu = new ContextMenuStrip();
        menu.Items.Add(Localization.T("TrayOpenDashboard"), null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem(Localization.T("TrayPauseAllMonitoring"));
        pause.DropDownItems.Add(Localization.T("Duration15Minutes"), null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromMinutes(15)));
        pause.DropDownItems.Add(Localization.T("Duration1Hour"), null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromHours(1)));
        pause.DropDownItems.Add(Localization.T("Duration4Hours"), null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromHours(4)));
        pause.DropDownItems.Add(Localization.T("DurationIndefinitely"), null, async (_, _) => await SetMaintenanceAsync(null));
        menu.Items.Add(pause);
        menu.Items.Add(Localization.T("TrayResumeAllMonitoring"), null, async (_, _) => await ResumeMaintenanceAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Localization.T("TrayRestartAppWatcher"), null, (_, _) => RestartAppWatcher());
        menu.Items.Add(Localization.T("TrayExitAppWatcher"), null, async (_, _) => await ExitAllAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = Localization.T("TrayMonitoring"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenDashboard();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        // IPC shutdown requests complete on a pipe worker thread. Posting to the captured
        // WinForms context keeps ApplicationContext shutdown on the message-loop thread.
        _engine.ExitRequestedTask.ContinueWith(
            _ => PostExit(),
            TaskScheduler.Default);

        SystemEvents.SessionEnding += OnSessionEnding;
        UpdateStatus();
    }

    private async Task SetMaintenanceAsync(TimeSpan? duration)
    {
        try
        {
            var config = await new ConfigService().LoadAsync();
            var seconds = duration is null ? null : (int?)duration.Value.TotalSeconds;
            var errors = await HostOperations.ExecuteAsync(config, new SupervisorRequest(SupervisorCommandType.StartMaintenance, DurationSeconds: seconds));
            if (errors.Count > 0) MessageBox.Show(string.Join(Environment.NewLine, errors), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localization.F("CouldNotPauseMonitoring", ex.Message), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ResumeMaintenanceAsync()
    {
        try
        {
            var config = await new ConfigService().LoadAsync();
            var errors = await HostOperations.ExecuteAsync(config, new SupervisorRequest(SupervisorCommandType.ResumeMaintenance));
            if (errors.Count > 0) MessageBox.Show(string.Join(Environment.NewLine, errors), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localization.F("CouldNotResumeMonitoring", ex.Message), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void UpdateStatus()
    {
        if (_updating || _exiting) return;
        _updating = true;
        try
        {
            var config = await LoadConfigIfChangedAsync();
            var helperRequired = config?.Applications.Any(a =>
                a.Privilege == PrivilegeLevel.Administrator && a.MonitoringEnabled) == true;

            var local = _engine.Snapshot();
            HostSnapshot? admin = null;
            var adminQueried = helperRequired || _helperWasOnline == true;
            if (adminQueried)
            {
                // 管理者アプリの異常もトレイに反映するため、Elevatedの状態も取得する。
                var response = await _adminClient.SendAsync(
                    new SupervisorRequest(SupervisorCommandType.GetSnapshot),
                    TimeSpan.FromMilliseconds(900));
                admin = response.Success ? response.Snapshot : null;
            }
            if (_exiting) return;

            var apps = local.Applications.Concat(admin?.Applications ?? []).ToArray();
            var helperOffline = helperRequired && admin is null;

            var notifications = TrayNotificationPlanner.Diff(_previousApps, apps).ToList();
            if (adminQueried && TrayNotificationPlanner.HelperWentOffline(_helperWasOnline, admin is not null, helperRequired))
            {
                notifications.Add(new TrayNotification(TrayNotificationKind.HelperOffline));
            }
            _previousApps = apps.ToDictionary(a => a.Id);
            if (adminQueried) _helperWasOnline = admin is not null;
            if (config?.Global.ShowNotifications != false) ShowNotifications(notifications);

            var problemCount = apps.Count(a => a.State is AppRuntimeState.Failed or AppRuntimeState.Backoff or AppRuntimeState.Unresponsive);
            var pausedCount = apps.Count(a => a.State == AppRuntimeState.Paused);

            if (local.MaintenanceActive || admin?.MaintenanceActive == true)
            {
                _notifyIcon.Icon = SystemIcons.Warning;
                _notifyIcon.Text = Truncate(Localization.T("TrayMaintenanceMode"));
            }
            else if (problemCount > 0 || helperOffline)
            {
                _notifyIcon.Icon = SystemIcons.Error;
                _notifyIcon.Text = Truncate(problemCount == 0
                    ? Localization.T("TrayHelperOffline")
                    : Localization.F("TrayProblems", problemCount + (helperOffline ? 1 : 0)));
            }
            else if (pausedCount > 0)
            {
                _notifyIcon.Icon = SystemIcons.Information;
                _notifyIcon.Text = Truncate(Localization.F("TrayPaused", pausedCount));
            }
            else
            {
                _notifyIcon.Icon = _appIcon;
                _notifyIcon.Text = Truncate(Localization.F("TrayMonitored", apps.Length));
            }
        }
        catch
        {
            // トレイ表示の更新失敗で監視ホストを止めない。
        }
        finally
        {
            _updating = false;
        }
    }

    private async Task<AppWatcherConfiguration?> LoadConfigIfChangedAsync()
    {
        var stamp = _configService.GetFileStamp();
        if (_config is null || stamp == default || stamp != _configStamp)
        {
            try
            {
                _config = await _configService.LoadAsync();
                _configStamp = stamp;
            }
            catch
            {
                // 読めない場合は前回の設定で表示を続ける。
            }
        }
        return _config;
    }

    private void ShowNotifications(IReadOnlyList<TrayNotification> notifications)
    {
        if (notifications.Count == 0) return;

        const int maxLines = 4;
        var lines = notifications
            .Take(maxLines)
            .Select(n => n.ApplicationName is null
                ? Localization.T($"Notify{n.Kind}")
                : Localization.F($"Notify{n.Kind}", n.ApplicationName))
            .ToList();
        if (notifications.Count > maxLines) lines.Add(Localization.F("NotifyMore", notifications.Count - maxLines));

        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 250) text = text[..250];
        var icon = notifications.All(n => n.Kind == TrayNotificationKind.Restarted) ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(10000, "AppWatcher", text, icon);
    }

    private static string Truncate(string value) => value.Length <= 63 ? value : value[..63];

    private static void OpenDashboard()
    {
        LaunchUiCommand(null, showErrors: true);
    }

    private void RestartAppWatcher()
    {
        var result = MessageBox.Show(
            Localization.T("RestartAppWatcherPrompt"),
            Localization.T("RestartAppWatcherTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return;
        LaunchUiCommand("--restart", showErrors: true);
    }

    private async Task ExitAllAsync()
    {
        var result = MessageBox.Show(
            Localization.T("ExitAppWatcherPrompt"),
            Localization.T("ExitAppWatcherTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return;

        LifecycleSignals.RequestUiExit();

        // Ask the elevated helper to exit itself. This avoids trying to terminate a
        // high-integrity process from the normal agent and therefore needs no new UAC prompt.
        await _adminClient.SendAsync(
            new SupervisorRequest(SupervisorCommandType.ShutdownHost),
            TimeSpan.FromSeconds(2));

        await _engine.RequestHostShutdownAsync();
        ExitThread();
    }

    private static void LaunchUiCommand(string? argument, bool showErrors)
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "AppWatcher.UI.exe");
        if (!File.Exists(uiPath))
        {
            if (showErrors)
            {
                MessageBox.Show(
                    Localization.F("DashboardNotFound", uiPath),
                    "AppWatcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uiPath,
                Arguments = argument ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(Localization.F("CouldNotOpenDashboard", ex.Message), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    private void PostExit()
    {
        try { _uiContext.Post(_ => ExitThread(), null); }
        catch { /* The message loop has already ended. */ }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        base.ExitThreadCore();
    }
}
