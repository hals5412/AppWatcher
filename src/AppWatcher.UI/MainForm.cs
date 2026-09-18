using System.Diagnostics;
using System.Text.Json;
using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class MainForm : Form
{
    private readonly ConfigService _configService = new();
    private readonly SupervisorClient _normalClient = new(PrivilegeLevel.Normal);
    private readonly SupervisorClient _adminClient = new(PrivilegeLevel.Administrator);
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly Label _hostStatus = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private HostSnapshot? _normalHost;
    private HostSnapshot? _adminHost;

    public MainForm()
    {
        Text = "AppWatcher";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1120;
        Height = 650;
        MinimumSize = new Size(900, 500);

        var menu = BuildMenu();
        MainMenuStrip = menu;
        Controls.Add(menu);

        _hostStatus.Dock = DockStyle.Top;
        _hostStatus.Height = 28;
        _hostStatus.Padding = new Padding(8, 6, 8, 0);
        Controls.Add(_hostStatus);

        ConfigureGrid();
        Controls.Add(_grid);

        var bottom = BuildBottomPanel();
        Controls.Add(bottom);

        _summary.AutoSize = false;
        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 26;
        _summary.Padding = new Padding(8, 5, 8, 0);
        Controls.Add(_summary);

        _refreshTimer.Interval = 2000;
        _refreshTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        Shown += async (_, _) =>
        {
            await EnsureNormalAgentStartedAsync();
            await RefreshDashboardAsync();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _refreshGate.Dispose();
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem(Localization.T("MenuFile"));
        file.DropDownItems.Add(Localization.T("OpenDataFolder"), null, (_, _) => OpenDataFolder());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Localization.T("CloseDashboard"), null, (_, _) => Close());

        var tools = new ToolStripMenuItem(Localization.T("MenuTools"));
        tools.DropDownItems.Add(Localization.T("Settings"), null, async (_, _) => await OpenSettingsAsync());
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Localization.T("InstallRepairStartupTasks"), null, (_, _) => Program.RequestStartupInstallation(this));
        tools.DropDownItems.Add(Localization.T("StartNormalAgentNow"), null, (_, _) => StartHost("AppWatcher.Agent.exe", elevated: false));
        tools.DropDownItems.Add(Localization.T("StartElevatedHelperNow"), null, (_, _) => StartHost("AppWatcher.Elevated.exe", elevated: true));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Localization.T("CreateDiagnosticPackage"), null, async (_, _) => await CreateDiagnosticPackageAsync());
        tools.DropDownItems.Add(Localization.T("ReloadConfiguration"), null, async (_, _) => await ReloadHostsAsync());

        var maintenance = new ToolStripMenuItem(Localization.T("MenuMaintenance"));
        maintenance.DropDownItems.Add(Localization.T("PauseAll15Minutes"), null, async (_, _) => await SetGlobalMaintenanceAsync(TimeSpan.FromMinutes(15)));
        maintenance.DropDownItems.Add(Localization.T("PauseAll1Hour"), null, async (_, _) => await SetGlobalMaintenanceAsync(TimeSpan.FromHours(1)));
        maintenance.DropDownItems.Add(Localization.T("PauseAll4Hours"), null, async (_, _) => await SetGlobalMaintenanceAsync(TimeSpan.FromHours(4)));
        maintenance.DropDownItems.Add(Localization.T("PauseAllIndefinitely"), null, async (_, _) => await SetGlobalMaintenanceAsync(null));
        maintenance.DropDownItems.Add(new ToolStripSeparator());
        maintenance.DropDownItems.Add(Localization.T("ResumeAllMonitoring"), null, async (_, _) => await ResumeGlobalMaintenanceAsync());

        menu.Items.Add(file);
        menu.Items.Add(tools);
        menu.Items.Add(maintenance);
        return menu;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.Fixed3D;
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await EditSelectedAsync();
        };

        _grid.Columns.Add(Column("Application", Localization.T("ColumnApplication"), 190));
        _grid.Columns.Add(Column("Status", Localization.T("ColumnStatus"), 105));
        _grid.Columns.Add(Column("Privilege", Localization.T("ColumnPrivilege"), 75));
        _grid.Columns.Add(Column("PID", "PID", 65));
        _grid.Columns.Add(Column("Uptime", Localization.T("ColumnUptime"), 105));
        _grid.Columns.Add(Column("Restarts", Localization.T("ColumnRestarts"), 70));
        _grid.Columns.Add(Column("LastEvent", Localization.T("ColumnLastEvent"), 160));
        _grid.Columns.Add(Column("Reason", Localization.T("ColumnReason"), 180));
        var path = Column("Executable", Localization.T("ColumnExecutable"), 280);
        path.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns.Add(path);
    }

    private static DataGridViewTextBoxColumn Column(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.Automatic
    };

    private Control BuildBottomPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6),
            WrapContents = false
        };

        panel.Controls.Add(Button(Localization.T("ButtonAdd"), async (_, _) => await AddAsync()));
        panel.Controls.Add(Button(Localization.T("ButtonEdit"), async (_, _) => await EditSelectedAsync()));
        panel.Controls.Add(Button(Localization.T("ButtonDelete"), async (_, _) => await DeleteSelectedAsync()));
        panel.Controls.Add(Spacer());
        panel.Controls.Add(Button(Localization.T("ButtonStart"), async (_, _) => await SendSelectedAsync(SupervisorCommandType.StartApplication)));
        panel.Controls.Add(Button(Localization.T("ButtonStop"), async (_, _) => await SendSelectedAsync(SupervisorCommandType.StopApplication)));
        panel.Controls.Add(Button(Localization.T("ButtonRestart"), async (_, _) => await SendSelectedAsync(SupervisorCommandType.RestartApplication)));

        var pauseButton = new Button { Text = Localization.T("ButtonPause"), AutoSize = true, Height = 28, MinimumSize = new Size(90, 28) };
        var pauseMenu = new ContextMenuStrip();
        pauseMenu.Items.Add(Localization.T("Duration15Minutes"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromMinutes(15)));
        pauseMenu.Items.Add(Localization.T("Duration1Hour"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(1)));
        pauseMenu.Items.Add(Localization.T("Duration4Hours"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(4)));
        pauseMenu.Items.Add(Localization.T("DurationIndefinitely"), null, async (_, _) => await PauseSelectedAsync(null));
        pauseButton.Click += (_, _) => pauseMenu.Show(pauseButton, new Point(0, pauseButton.Height));
        panel.Controls.Add(pauseButton);
        panel.Controls.Add(Button(Localization.T("ButtonResume"), async (_, _) => await SendSelectedAsync(SupervisorCommandType.ResumeApplication)));
        panel.Controls.Add(Spacer());
        panel.Controls.Add(Button(Localization.T("ButtonLogs"), (_, _) => new EventLogForm().Show(this)));
        panel.Controls.Add(Button(Localization.T("ButtonRefresh"), async (_, _) => await RefreshDashboardAsync()));
        return panel;
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 28, MinimumSize = new Size(64, 28) };
        button.Click += handler;
        return button;
    }

    private static Control Spacer() => new Panel { Width = 12, Height = 28 };

    private async Task EnsureNormalAgentStartedAsync()
    {
        var ping = await _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.Ping), TimeSpan.FromMilliseconds(350));
        if (ping.Success) return;
        StartHost("AppWatcher.Agent.exe", elevated: false, showErrors: false);
        await Task.Delay(500);
    }

    private void StartHost(string fileName, bool elevated, bool showErrors = true)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            if (showErrors)
            {
                MessageBox.Show(this, Localization.F("HostExecutableNotFound", path), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC cancelled.
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            var selectedId = SelectedSnapshot()?.Id;
            var normalTask = _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.GetSnapshot), TimeSpan.FromMilliseconds(900));
            var adminTask = _adminClient.SendAsync(new SupervisorRequest(SupervisorCommandType.GetSnapshot), TimeSpan.FromMilliseconds(900));
            await Task.WhenAll(normalTask, adminTask);

            var normal = await normalTask;
            var admin = await adminTask;
            _normalHost = normal.Success ? normal.Snapshot : null;
            _adminHost = admin.Success ? admin.Snapshot : null;

            var apps = new List<ApplicationSnapshot>();
            if (_normalHost is not null) apps.AddRange(_normalHost.Applications);
            if (_adminHost is not null) apps.AddRange(_adminHost.Applications);

            _grid.Rows.Clear();
            foreach (var app in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var rowIndex = _grid.Rows.Add(
                    app.Name,
                    Localization.StateText(app.State),
                    Localization.PrivilegeText(app.Privilege),
                    app.ProcessId?.ToString() ?? "-",
                    FormatUptime(app.Uptime),
                    app.RestartCountInWindow,
                    Localization.EventCode(app.LastEvent),
                    Localization.ReasonCode(app.LastReason),
                    app.ExecutablePath);
                var row = _grid.Rows[rowIndex];
                row.Tag = app;
                ApplyStateStyle(row, app.State);
                if (selectedId == app.Id) row.Selected = true;
            }

            var healthy = apps.Count(a => a.State == AppRuntimeState.Healthy);
            var paused = apps.Count(a => a.State == AppRuntimeState.Paused);
            var problems = apps.Count(a => a.State is AppRuntimeState.Failed or AppRuntimeState.Backoff or AppRuntimeState.Unresponsive);
            _summary.Text = Localization.F("SummaryFormat", healthy, paused, problems, apps.Count);
            UpdateHostStatus(normal.Error, admin.Error);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void UpdateHostStatus(string? normalError, string? adminError)
    {
        var normalText = _normalHost is null
            ? Localization.T("AgentOffline")
            : Localization.F("AgentStatusFormat", _normalHost.HostProcessId, FormatUptime(_normalHost.HostUptime), _normalHost.WorkingSetBytes / 1024d / 1024d);
        var adminText = _adminHost is null
            ? Localization.T("ElevatedOffline")
            : Localization.F("ElevatedStatusFormat", _adminHost.HostProcessId, FormatUptime(_adminHost.HostUptime), _adminHost.WorkingSetBytes / 1024d / 1024d);
        var maintenance = (_normalHost?.MaintenanceActive == true || _adminHost?.MaintenanceActive == true) ? $"    {Localization.T("MaintenanceActive")}" : string.Empty;
        _hostStatus.Text = $"{normalText}    |    {adminText}{maintenance}";
        _hostStatus.ForeColor = _normalHost is null ? Color.DarkRed : maintenance.Length > 0 ? Color.DarkGoldenrod : SystemColors.ControlText;
        _hostStatus.Cursor = Cursors.Hand;
        _hostStatus.Tag = new { normalError, adminError };
    }

    private static void ApplyStateStyle(DataGridViewRow row, AppRuntimeState state)
    {
        row.DefaultCellStyle.ForeColor = state switch
        {
            AppRuntimeState.Failed or AppRuntimeState.Backoff => Color.DarkRed,
            AppRuntimeState.Unresponsive => Color.DarkOrange,
            AppRuntimeState.Paused => Color.DimGray,
            _ => SystemColors.ControlText
        };
    }

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null) return "-";
        var value = uptime.Value;
        return value.TotalDays >= 1
            ? Localization.F("UptimeDaysFormat", (int)value.TotalDays, value.Hours, value.Minutes)
            : $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private ApplicationSnapshot? SelectedSnapshot() =>
        _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as ApplicationSnapshot;

    private async Task AddAsync()
    {
        var definition = new ApplicationDefinition();
        using var editor = new AppEditForm(definition);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        var config = await _configService.LoadAsync();
        config.Applications.Add(editor.Result);
        await _configService.SaveAsync(config);
        await ReloadHostsAsync();
    }

    private async Task EditSelectedAsync()
    {
        var selected = SelectedSnapshot();
        if (selected is null) return;

        var config = await _configService.LoadAsync();
        var existing = config.Applications.FirstOrDefault(a => a.Id == selected.Id);
        if (existing is null) return;

        var clone = JsonSerializer.Deserialize<ApplicationDefinition>(
            JsonSerializer.Serialize(existing, JsonDefaults.Options), JsonDefaults.Options) ?? existing;

        using var editor = new AppEditForm(clone);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        var index = config.Applications.FindIndex(a => a.Id == selected.Id);
        if (index >= 0) config.Applications[index] = editor.Result;
        await _configService.SaveAsync(config);
        await ReloadHostsAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedSnapshot();
        if (selected is null) return;
        if (MessageBox.Show(this,
                Localization.F("RemoveApplicationPrompt", selected.Name),
                Localization.T("RemoveApplicationTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var config = await _configService.LoadAsync();
        config.Applications.RemoveAll(a => a.Id == selected.Id);
        await _configService.SaveAsync(config);
        await ReloadHostsAsync();
    }

    private async Task ReloadHostsAsync()
    {
        await Task.WhenAll(
            _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.ReloadConfiguration)),
            _adminClient.SendAsync(new SupervisorRequest(SupervisorCommandType.ReloadConfiguration)));
        await RefreshDashboardAsync();
    }

    private async Task SendSelectedAsync(SupervisorCommandType command)
    {
        var selected = SelectedSnapshot();
        if (selected is null) return;
        var client = selected.Privilege == PrivilegeLevel.Administrator ? _adminClient : _normalClient;
        var response = await client.SendAsync(new SupervisorRequest(command, selected.Id));
        if (!response.Success)
        {
            MessageBox.Show(this,
                Localization.F("CommandFailed", selected.Name, response.Error),
                "AppWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        await RefreshDashboardAsync();
    }

    private async Task PauseSelectedAsync(TimeSpan? duration)
    {
        var selected = SelectedSnapshot();
        if (selected is null) return;
        var client = selected.Privilege == PrivilegeLevel.Administrator ? _adminClient : _normalClient;
        var seconds = duration is null ? null : (int?)Math.Clamp((long)duration.Value.TotalSeconds, 1, int.MaxValue);
        var response = await client.SendAsync(new SupervisorRequest(SupervisorCommandType.PauseApplication, selected.Id, seconds));
        if (!response.Success)
        {
            MessageBox.Show(this, response.Error, "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        await RefreshDashboardAsync();
    }

    private async Task SetGlobalMaintenanceAsync(TimeSpan? duration)
    {
        var seconds = duration is null ? null : (int?)Math.Clamp((long)duration.Value.TotalSeconds, 1, int.MaxValue);
        await Task.WhenAll(
            _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.StartMaintenance, DurationSeconds: seconds)),
            _adminClient.SendAsync(new SupervisorRequest(SupervisorCommandType.StartMaintenance, DurationSeconds: seconds)));
        await RefreshDashboardAsync();
    }

    private async Task ResumeGlobalMaintenanceAsync()
    {
        await Task.WhenAll(
            _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.ResumeMaintenance)),
            _adminClient.SendAsync(new SupervisorRequest(SupervisorCommandType.ResumeMaintenance)));
        await RefreshDashboardAsync();
    }

    private async Task OpenSettingsAsync()
    {
        using var settings = new SettingsForm();
        settings.ShowDialog(this);
        await RefreshDashboardAsync();
    }

    private async Task CreateDiagnosticPackageAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Localization.T("ZipFilter"),
            FileName = $"AppWatcher-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Title = Localization.T("SaveDiagnosticPackageTitle")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var builder = new DiagnosticPackageBuilder(_configService);
            await builder.CreateAsync(dialog.FileName, _normalHost, _adminHost);
            MessageBox.Show(this, Localization.T("DiagnosticPackageCreated"), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Localization.T("DiagnosticPackageFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDataFolder()
    {
        AppPaths.EnsureDataDirectory();
        Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDirectory, UseShellExecute = true });
    }
}
