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
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause all monitoring");
        pause.DropDownItems.Add("15 minutes", null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromMinutes(15)));
        pause.DropDownItems.Add("1 hour", null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromHours(1)));
        pause.DropDownItems.Add("4 hours", null, async (_, _) => await SetMaintenanceAsync(TimeSpan.FromHours(4)));
        pause.DropDownItems.Add("Indefinitely", null, async (_, _) => await SetMaintenanceAsync(null));
        menu.Items.Add(pause);
        menu.Items.Add("Resume all monitoring", null, async (_, _) => await ResumeMaintenanceAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit AppWatcher Agent", null, (_, _) => ExitAgent());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AppWatcher - Monitoring",
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
            MessageBox.Show($"Could not pause monitoring.\n\n{ex.Message}", "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            MessageBox.Show($"Could not resume monitoring.\n\n{ex.Message}", "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _notifyIcon.Text = Truncate("AppWatcher - Maintenance mode");
        }
        else if (problemCount > 0)
        {
            _notifyIcon.Icon = SystemIcons.Error;
            _notifyIcon.Text = Truncate($"AppWatcher - {problemCount} problem(s)");
        }
        else if (pausedCount > 0)
        {
            _notifyIcon.Icon = SystemIcons.Information;
            _notifyIcon.Text = Truncate($"AppWatcher - {pausedCount} paused");
        }
        else
        {
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Text = Truncate($"AppWatcher - {snapshot.Applications.Count} monitored");
        }
    }

    private static string Truncate(string value) => value.Length <= 63 ? value : value[..63];

    private static void OpenDashboard()
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "AppWatcher.UI.exe");
        if (!File.Exists(uiPath))
        {
            MessageBox.Show(
                $"Dashboard executable was not found next to the Agent.\n\nExpected:\n{uiPath}",
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
            MessageBox.Show($"Could not open the dashboard.\n\n{ex.Message}", "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    private void ExitAgent()
    {
        var result = MessageBox.Show(
            "Stop AppWatcher monitoring?\n\nManaged applications will be left running.",
            "Exit AppWatcher Agent",
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
