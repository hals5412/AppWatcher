using System.Diagnostics;
using AppWatcher.Core;
using Microsoft.Win32;

namespace AppWatcher.Agent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SupervisorEngine _engine;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _statusTimer;

    public TrayApplicationContext(SupervisorEngine engine)
    {
        _engine = engine;

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
        menu.Items.Add(Localization.T("TrayExitAgent"), null, (_, _) => ExitAgent());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Localization.T("TrayMonitoring"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenDashboard();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        SystemEvents.SessionEnding += OnSessionEnding;
        UpdateStatus();
    }

    private async Task SetMaintenanceAsync(TimeSpan? duration)
    {
        try
        {
            await _engine.SetMaintenanceAsync(duration);
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
            await _engine.ResumeMaintenanceAsync();
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
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Text = Truncate(Localization.F("TrayMonitored", snapshot.Applications.Count));
        }
    }

    private static string Truncate(string value) => value.Length <= 63 ? value : value[..63];

    private static void OpenDashboard()
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "AppWatcher.UI.exe");
        if (!File.Exists(uiPath))
        {
            MessageBox.Show(
                Localization.F("DashboardNotFound", uiPath),
                "AppWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uiPath,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localization.F("CouldNotOpenDashboard", ex.Message), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    private void ExitAgent()
    {
        var result = MessageBox.Show(
            Localization.T("ExitAgentPrompt"),
            Localization.T("ExitAgentTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return;
        _engine.BeginSystemShutdown();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
