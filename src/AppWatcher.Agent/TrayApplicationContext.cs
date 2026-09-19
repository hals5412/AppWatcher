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
    private readonly System.Windows.Forms.Timer _exitTimer;

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

        // IPC shutdown requests originate on a pipe worker thread. Polling this flag from
        // the WinForms message loop keeps ApplicationContext shutdown on the correct thread.
        _exitTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _exitTimer.Tick += (_, _) =>
        {
            if (_engine.ExitRequested) ExitThread();
        };
        _exitTimer.Start();

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

    private void UpdateStatus()
    {
        var snapshot = _engine.Snapshot();
        var problemCount = snapshot.Applications.Count(a => a.State is AppRuntimeState.Failed or AppRuntimeState.Backoff or AppRuntimeState.Unresponsive);
        var pausedCount = snapshot.Applications.Count(a => a.State == AppRuntimeState.Paused);

        if (snapshot.MaintenanceActive)
        {
            _notifyIcon.Icon = SystemIcons.Warning;
            _notifyIcon.Text = Truncate(Localization.T("TrayMaintenanceMode"));
        }
        else if (problemCount > 0)
        {
            _notifyIcon.Icon = SystemIcons.Error;
            _notifyIcon.Text = Truncate(Localization.F("TrayProblems", problemCount));
        }
        else if (pausedCount > 0)
        {
            _notifyIcon.Icon = SystemIcons.Information;
            _notifyIcon.Text = Truncate(Localization.F("TrayPaused", pausedCount));
        }
        else
        {
            _notifyIcon.Icon = _appIcon;
            _notifyIcon.Text = Truncate(Localization.F("TrayMonitored", snapshot.Applications.Count));
        }
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

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _exitTimer.Stop();
        _exitTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        base.ExitThreadCore();
    }
}
