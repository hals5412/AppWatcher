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
    private readonly ContextMenuStrip _rowContextMenu = new();

    private readonly Button _editButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _restartButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();

    private readonly ToolStripMenuItem _contextEditItem = new();
    private readonly ToolStripMenuItem _contextDeleteItem = new();
    private readonly ToolStripMenuItem _contextStartItem = new();
    private readonly ToolStripMenuItem _contextStopItem = new();
    private readonly ToolStripMenuItem _contextRestartItem = new();
    private readonly ToolStripMenuItem _contextPauseItem = new();
    private readonly ToolStripMenuItem _contextResumeItem = new();

    private readonly Label _summary = new();
    private readonly Label _hostStatus = new();
    private readonly LinkLabel _startupWarning = new();
    private readonly TableLayoutPanel _mainLayout = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly System.Windows.Forms.Timer _lifecycleTimer = new();
    private readonly EventWaitHandle _uiExitEvent = LifecycleSignals.CreateUiExitEvent();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private HostSnapshot? _normalHost;
    private HostSnapshot? _adminHost;
    private StartupTasksStatus? _cachedStartupTasksStatus;
    private DateTimeOffset _nextStartupTaskCheckUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _startupSelfCheckIssues = [];
    private bool _columnLayoutLoaded;
    private bool _columnLayoutDirty;

    public MainForm()
    {
        Text = "AppWatcher";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 1180;
        Height = 680;
        MinimumSize = new Size(940, 520);

        var menu = BuildMenu();
        MainMenuStrip = menu;

        _hostStatus.Dock = DockStyle.Fill;
        _hostStatus.Padding = new Padding(8, 8, 8, 0);

        _startupWarning.Dock = DockStyle.Fill;
        _startupWarning.AutoSize = false;
        _startupWarning.Padding = new Padding(8, 5, 8, 0);
        _startupWarning.BackColor = SystemColors.Info;
        _startupWarning.ForeColor = SystemColors.InfoText;
        _startupWarning.LinkColor = SystemColors.InfoText;
        _startupWarning.ActiveLinkColor = SystemColors.HotTrack;
        _startupWarning.LinkBehavior = LinkBehavior.HoverUnderline;
        _startupWarning.Visible = false;
        _startupWarning.LinkClicked += async (_, _) => await ShowStartupSelfCheckDetailsAsync();

        ConfigureGrid();
        ConfigureRowContextMenu();

        var bottom = BuildBottomPanel();

        _summary.AutoSize = false;
        _summary.Dock = DockStyle.Fill;
        _summary.Padding = new Padding(8, 7, 8, 0);

        // Keep the grid in its own layout row. The startup self-check warning row
        // collapses to zero height while the startup configuration is healthy.
        _mainLayout.Dock = DockStyle.Fill;
        _mainLayout.ColumnCount = 1;
        _mainLayout.RowCount = 5;
        _mainLayout.Margin = Padding.Empty;
        _mainLayout.Padding = Padding.Empty;
        _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _mainLayout.Controls.Add(_hostStatus, 0, 0);
        _mainLayout.Controls.Add(_startupWarning, 0, 1);
        _mainLayout.Controls.Add(_grid, 0, 2);
        _mainLayout.Controls.Add(bottom, 0, 3);
        _mainLayout.Controls.Add(_summary, 0, 4);

        Controls.Add(_mainLayout);
        Controls.Add(menu);

        _refreshTimer.Interval = 2000;
        _refreshTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        _lifecycleTimer.Interval = 200;
        _lifecycleTimer.Tick += (_, _) =>
        {
            if (_uiExitEvent.WaitOne(0)) Close();
        };
        _lifecycleTimer.Start();
        Shown += async (_, _) =>
        {
            await LoadColumnLayoutAsync();
            await EnsureNormalAgentStartedAsync();
            await EnsureConfiguredHostsStartedAsync();
            await RefreshDashboardAsync();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            SaveColumnLayoutIfNeeded();
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _lifecycleTimer.Stop();
            _lifecycleTimer.Dispose();
            _rowContextMenu.Dispose();
            _uiExitEvent.Dispose();
            _refreshGate.Dispose();
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem(Localization.T("MenuFile"));
        file.DropDownItems.Add(Localization.T("OpenDataFolder"), null, (_, _) => OpenDataFolder());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Localization.T("MenuRestartAppWatcher"), null, async (_, _) => await RestartAppWatcherAsync());
        file.DropDownItems.Add(Localization.T("MenuExitAppWatcher"), null, async (_, _) => await ExitAppWatcherAsync());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Localization.T("CloseDashboard"), null, (_, _) => Close());

        var tools = new ToolStripMenuItem(Localization.T("MenuTools"));
        tools.DropDownItems.Add(Localization.T("Settings"), null, async (_, _) => await OpenSettingsAsync());
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Localization.T("InstallRepairStartupTasks"), null, (_, _) => Program.RequestStartupInstallation(this));
        tools.DropDownItems.Add(Localization.T("StartupTaskStatusMenu"), null, (_, _) => ShowStartupTaskStatus());
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
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.RowHeadersVisible = false;
        _grid.ColumnHeadersVisible = true;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 32;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.RowTemplate.Height = 30;
        _grid.DefaultCellStyle.Padding = new Padding(2, 1, 2, 1);
        _grid.AllowUserToResizeColumns = true;
        _grid.AllowUserToOrderColumns = true;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.ShowCellToolTips = true;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.Fixed3D;
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            e.ToolTipText = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].FormattedValue?.ToString();
        };
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await EditSelectedAsync();
        };
        _grid.CellMouseDown += GridOnCellMouseDown;
        _grid.SelectionChanged += (_, _) => UpdateCommandAvailability();
        _grid.ColumnWidthChanged += (_, _) =>
        {
            if (_columnLayoutLoaded) _columnLayoutDirty = true;
        };
        _grid.ColumnDisplayIndexChanged += (_, _) =>
        {
            if (_columnLayoutLoaded) _columnLayoutDirty = true;
        };

        _grid.Columns.Add(Column("Application", Localization.T("ColumnApplication"), 170));
        _grid.Columns.Add(Column("Status", Localization.T("ColumnStatus"), 110));
        _grid.Columns.Add(Column("Privilege", Localization.T("ColumnPrivilege"), 90));
        _grid.Columns.Add(Column("PID", "PID", 70));
        _grid.Columns.Add(Column("Uptime", Localization.T("ColumnUptime"), 105));
        _grid.Columns.Add(Column("Restarts", Localization.T("ColumnRestarts"), 95));
        _grid.Columns.Add(Column("LastEvent", Localization.T("ColumnLastEvent"), 170));
        _grid.Columns.Add(Column("Reason", Localization.T("ColumnReason"), 210));
        _grid.Columns.Add(Column("Executable", Localization.T("ColumnExecutable"), 420));
    }

    private static DataGridViewTextBoxColumn Column(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        MinimumWidth = 50,
        SortMode = DataGridViewColumnSortMode.Automatic,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.None
    };

    private async Task LoadColumnLayoutAsync()
    {
        var config = await _configService.LoadAsync();
        var saved = config.Global.DashboardColumns ?? [];

        _columnLayoutLoaded = false;
        try
        {
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                if (saved.TryGetValue(column.Name, out var layout) && layout.Width >= column.MinimumWidth)
                {
                    column.Width = Math.Clamp(layout.Width, column.MinimumWidth, 2400);
                }
            }

            var ordered = _grid.Columns
                .Cast<DataGridViewColumn>()
                .OrderBy(column => saved.TryGetValue(column.Name, out var layout)
                    ? layout.DisplayIndex
                    : column.DisplayIndex)
                .ThenBy(column => column.Index)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].DisplayIndex = i;
            }
        }
        finally
        {
            _columnLayoutLoaded = true;
            _columnLayoutDirty = false;
        }
    }

    private void SaveColumnLayoutIfNeeded()
    {
        if (!_columnLayoutLoaded || !_columnLayoutDirty) return;

        try
        {
            var config = _configService.LoadAsync().GetAwaiter().GetResult();
            config.Global.DashboardColumns = _grid.Columns
                .Cast<DataGridViewColumn>()
                .ToDictionary(
                    column => column.Name,
                    column => new DashboardColumnLayout
                    {
                        Width = column.Width,
                        DisplayIndex = column.DisplayIndex
                    },
                    StringComparer.Ordinal);

            _configService.UpdateAsync(latest => latest.Global.DashboardColumns = config.Global.DashboardColumns).GetAwaiter().GetResult();
            _columnLayoutDirty = false;
        }
        catch (Exception ex)
        {
            try
            {
                AppPaths.EnsureDataDirectory();
                new AppWatcher.Core.FallbackLog(AppPaths.FallbackLog).WriteAsync($"{DateTimeOffset.UtcNow:O}`tWarning`tDashboardColumnLayoutSaveFailed`t{ex}{Environment.NewLine}").GetAwaiter().GetResult();
            }
            catch
            {
                // UI shutdown must continue even if preferences cannot be saved.
            }
        }
    }

    private void ConfigureRowContextMenu()
    {
        _rowContextMenu.Items.Clear();

        _rowContextMenu.Items.Add(
            Localization.T("ButtonAdd"),
            null,
            async (_, _) => await AddAsync());

        ConfigureContextItem(
            _contextEditItem,
            Localization.T("ButtonEdit"),
            async (_, _) => await EditSelectedAsync());
        ConfigureContextItem(
            _contextDeleteItem,
            Localization.T("ButtonDelete"),
            async (_, _) => await DeleteSelectedAsync());

        _rowContextMenu.Items.Add(_contextEditItem);
        _rowContextMenu.Items.Add(_contextDeleteItem);
        _rowContextMenu.Items.Add(new ToolStripSeparator());

        ConfigureContextItem(
            _contextStartItem,
            Localization.T("ButtonStart"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.StartApplication));
        ConfigureContextItem(
            _contextStopItem,
            Localization.T("ButtonStop"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.StopApplication));
        ConfigureContextItem(
            _contextRestartItem,
            Localization.T("ButtonRestart"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.RestartApplication));

        _rowContextMenu.Items.Add(_contextStartItem);
        _rowContextMenu.Items.Add(_contextStopItem);
        _rowContextMenu.Items.Add(_contextRestartItem);

        _contextPauseItem.Text = Localization.T("ButtonPause").Trim().TrimEnd('▼').TrimEnd();
        _contextPauseItem.DropDownItems.Clear();
        _contextPauseItem.DropDownItems.Add(
            Localization.T("Duration15Minutes"),
            null,
            async (_, _) => await PauseSelectedAsync(TimeSpan.FromMinutes(15)));
        _contextPauseItem.DropDownItems.Add(
            Localization.T("Duration1Hour"),
            null,
            async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(1)));
        _contextPauseItem.DropDownItems.Add(
            Localization.T("Duration4Hours"),
            null,
            async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(4)));
        _contextPauseItem.DropDownItems.Add(
            Localization.T("DurationIndefinitely"),
            null,
            async (_, _) => await PauseSelectedAsync(null));
        _rowContextMenu.Items.Add(_contextPauseItem);

        ConfigureContextItem(
            _contextResumeItem,
            Localization.T("ButtonResume"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.ResumeApplication));
        _rowContextMenu.Items.Add(_contextResumeItem);

        _rowContextMenu.Items.Add(new ToolStripSeparator());
        _rowContextMenu.Items.Add(
            Localization.T("ButtonLogs"),
            null,
            (_, _) => OpenSelectedLogs());

        UpdateCommandAvailability();
    }

    private void OpenSelectedLogs()
    {
        var selected = SelectedSnapshot();
        if (selected is null)
        {
            return;
        }

        new EventLogForm(
            selected.Id,
            selected.Name).Show(this);
    }

    private static void ConfigureContextItem(
        ToolStripMenuItem item,
        string text,
        EventHandler handler)
    {
        item.Text = text;
        item.Click += handler;
    }

    private void GridOnCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0)
        {
            return;
        }

        _grid.ClearSelection();

        var row = _grid.Rows[e.RowIndex];
        row.Selected = true;

        if (e.ColumnIndex >= 0)
        {
            _grid.CurrentCell = row.Cells[e.ColumnIndex];
        }
        else
        {
            var firstVisibleColumn = _grid.Columns
                .Cast<DataGridViewColumn>()
                .Where(column => column.Visible)
                .OrderBy(column => column.DisplayIndex)
                .FirstOrDefault();

            if (firstVisibleColumn is not null)
            {
                _grid.CurrentCell = row.Cells[firstVisibleColumn.Index];
            }
        }

        UpdateCommandAvailability();
        _rowContextMenu.Show(_grid, e.Location);
    }

    private Control BuildBottomPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(7),
            WrapContents = false
        };

        panel.Controls.Add(Button(Localization.T("ButtonAdd"), async (_, _) => await AddAsync()));
        panel.Controls.Add(Button(
            Localization.T("ButtonAddRunning"),
            async (_, _) => await AddRunningApplicationAsync()));

        ConfigureButton(_editButton, Localization.T("ButtonEdit"), async (_, _) => await EditSelectedAsync());
        ConfigureButton(_deleteButton, Localization.T("ButtonDelete"), async (_, _) => await DeleteSelectedAsync());
        panel.Controls.Add(_editButton);
        panel.Controls.Add(_deleteButton);

        panel.Controls.Add(Spacer());

        ConfigureButton(
            _startButton,
            Localization.T("ButtonStart"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.StartApplication));
        ConfigureButton(
            _stopButton,
            Localization.T("ButtonStop"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.StopApplication));
        ConfigureButton(
            _restartButton,
            Localization.T("ButtonRestart"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.RestartApplication));

        panel.Controls.Add(_startButton);
        panel.Controls.Add(_stopButton);
        panel.Controls.Add(_restartButton);

        ConfigureButton(_pauseButton, Localization.T("ButtonPause"), (_, _) => { }, minimumWidth: 96);
        var pauseMenu = new ContextMenuStrip();
        pauseMenu.Items.Add(Localization.T("Duration15Minutes"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromMinutes(15)));
        pauseMenu.Items.Add(Localization.T("Duration1Hour"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(1)));
        pauseMenu.Items.Add(Localization.T("Duration4Hours"), null, async (_, _) => await PauseSelectedAsync(TimeSpan.FromHours(4)));
        pauseMenu.Items.Add(Localization.T("DurationIndefinitely"), null, async (_, _) => await PauseSelectedAsync(null));
        _pauseButton.Click += (_, _) => pauseMenu.Show(_pauseButton, new Point(0, _pauseButton.Height));
        panel.Controls.Add(_pauseButton);

        ConfigureButton(
            _resumeButton,
            Localization.T("ButtonResume"),
            async (_, _) => await SendSelectedAsync(SupervisorCommandType.ResumeApplication));
        panel.Controls.Add(_resumeButton);

        panel.Controls.Add(Spacer());
        panel.Controls.Add(Button(Localization.T("ButtonLogs"), (_, _) => new EventLogForm().Show(this)));

        UpdateCommandAvailability();
        return panel;
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button();
        ConfigureButton(button, text, handler);
        return button;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        EventHandler handler,
        int minimumWidth = 72)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 32;
        button.MinimumSize = new Size(minimumWidth, 32);
        button.Click += handler;
    }

    private void UpdateCommandAvailability()
    {
        var selected = SelectedSnapshot();
        var hasSelection = selected is not null;

        var state = selected?.State ?? AppRuntimeState.Unknown;
        var running = selected?.ProcessId is not null;
        var unavailable = !hasSelection || state == AppRuntimeState.Unknown;
        var transitional = state is AppRuntimeState.Starting or AppRuntimeState.Restarting;
        var paused = state == AppRuntimeState.Paused;
        var globalMaintenance = selected is not null &&
            (selected.Privilege == PrivilegeLevel.Administrator
                ? _adminHost?.MaintenanceActive == true
                : _normalHost?.MaintenanceActive == true);

        var canEdit = hasSelection;
        var canDelete = hasSelection;

        // Start is useful for a genuinely stopped/failed/backoff application,
        // but not while it is already running, paused, transitioning, or offline.
        var canStart = !unavailable &&
                       !transitional &&
                       !paused &&
                       !running;

        // Stop remains useful while monitoring is paused if the process itself
        // is still running.
        var canStop = !unavailable &&
                      (running || state is AppRuntimeState.Restarting or AppRuntimeState.Backoff);

        // Restart only makes semantic sense for a currently running application.
        // While paused, restarting would stop it and then be suppressed by pause.
        var canRestart = !unavailable &&
                         !transitional &&
                         !paused &&
                         running;

        // Pause can also be useful in Stopped/Failed/Backoff states because it
        // prevents later automatic recovery. Resume is only meaningful for an
        // application-level pause while monitoring is enabled and global
        // maintenance is not active.
        var canPause = !unavailable &&
                       !transitional &&
                       !paused &&
                       selected!.MonitoringEnabled;

        var canResume = !unavailable &&
                        paused &&
                        selected!.MonitoringEnabled &&
                        !globalMaintenance;

        _editButton.Enabled = canEdit;
        _deleteButton.Enabled = canDelete;
        _startButton.Enabled = canStart;
        _stopButton.Enabled = canStop;
        _restartButton.Enabled = canRestart;
        _pauseButton.Enabled = canPause;
        _resumeButton.Enabled = canResume;

        _contextEditItem.Enabled = canEdit;
        _contextDeleteItem.Enabled = canDelete;
        _contextStartItem.Enabled = canStart;
        _contextStopItem.Enabled = canStop;
        _contextRestartItem.Enabled = canRestart;
        _contextPauseItem.Enabled = canPause;
        _contextResumeItem.Enabled = canResume;
    }

    private static Control Spacer() => new Panel { Width = 12, Height = 32 };

    private void ShowStartupTaskStatus()
    {
        var status = TaskSchedulerInstaller.GetStatus();
        _cachedStartupTasksStatus = status;
        _nextStartupTaskCheckUtc = DateTimeOffset.UtcNow.AddSeconds(15);

        var expectedAgent = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Agent.exe");
        var expectedElevated = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Elevated.exe");

        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            FormatStartupTaskStatus(
                Localization.T("StartupTaskAgentLabel"),
                status.Agent,
                expectedAgent),
            FormatStartupTaskStatus(
                Localization.T("StartupTaskElevatedLabel"),
                status.Elevated,
                expectedElevated));

        MessageBox.Show(
            this,
            text,
            Localization.T("StartupTaskStatusTitle"),
            MessageBoxButtons.OK,
            IsStartupTaskHealthy(status.Agent, expectedAgent) &&
            IsStartupTaskHealthy(status.Elevated, expectedElevated)
                ? MessageBoxIcon.Information
                : MessageBoxIcon.Warning);
    }

    private void UpdateStartupSelfCheck(
        AppWatcherConfiguration config,
        bool forceTaskRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (forceTaskRefresh ||
            _cachedStartupTasksStatus is null ||
            now >= _nextStartupTaskCheckUtc)
        {
            _cachedStartupTasksStatus = TaskSchedulerInstaller.GetStatus();
            _nextStartupTaskCheckUtc = now.AddSeconds(15);
        }

        var issues = new List<string>();
        var expectedAgent = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Agent.exe");
        var expectedElevated = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Elevated.exe");

        if (!File.Exists(expectedAgent))
        {
            issues.Add(Localization.T("StartupSelfCheckAgentExecutableMissing"));
        }

        if (!File.Exists(expectedElevated))
        {
            issues.Add(Localization.T("StartupSelfCheckElevatedExecutableMissing"));
        }

        var taskStatus = _cachedStartupTasksStatus;
        if (taskStatus is not null)
        {
            var taskStatusError =
                taskStatus.Agent.Error ??
                taskStatus.Elevated.Error;

            if (!string.IsNullOrWhiteSpace(taskStatusError))
            {
                issues.Add(Localization.T("StartupSelfCheckTaskStatusUnavailable"));
            }
            else
            {
                AddStartupTaskIssues(
                    issues,
                    taskStatus.Agent,
                    expectedAgent,
                    "StartupSelfCheckAgentTaskMissing",
                    "StartupSelfCheckAgentTaskDisabled",
                    "StartupSelfCheckAgentTaskRegistrationMismatch");

                AddStartupTaskIssues(
                    issues,
                    taskStatus.Elevated,
                    expectedElevated,
                    "StartupSelfCheckElevatedTaskMissing",
                    "StartupSelfCheckElevatedTaskDisabled",
                    "StartupSelfCheckElevatedTaskRegistrationMismatch");
            }
        }

        if (_normalHost is null)
        {
            issues.Add(Localization.T("StartupSelfCheckAgentOffline"));
        }

        var administratorHostRequired = config.Applications.Any(application =>
            application.Privilege == PrivilegeLevel.Administrator &&
            application.MonitoringEnabled);

        if (administratorHostRequired && _adminHost is null)
        {
            issues.Add(Localization.T("StartupSelfCheckElevatedOfflineRequired"));
        }

        _startupSelfCheckIssues = issues
            .Distinct(StringComparer.CurrentCulture)
            .ToArray();

        if (_startupSelfCheckIssues.Count == 0)
        {
            _startupWarning.Visible = false;
            _startupWarning.Text = string.Empty;
            _startupWarning.Links.Clear();
            _mainLayout.RowStyles[1].Height = 0;
            return;
        }

        var firstIssue = _startupSelfCheckIssues[0];
        _startupWarning.Text = Localization.F(
            "StartupSelfCheckWarningFormat",
            _startupSelfCheckIssues.Count,
            firstIssue);
        _startupWarning.Links.Clear();
        _startupWarning.Links.Add(0, _startupWarning.Text.Length);
        _startupWarning.Visible = true;
        _mainLayout.RowStyles[1].Height = 32;
    }

    private static void AddStartupTaskIssues(
        ICollection<string> issues,
        StartupTaskInfo info,
        string expectedExecutable,
        string missingResource,
        string disabledResource,
        string mismatchResource)
    {
        if (!info.Installed)
        {
            issues.Add(Localization.T(missingResource));
            return;
        }

        if (!info.Enabled)
        {
            issues.Add(Localization.T(disabledResource));
        }

        if (!StartupTaskRegistrationMatches(info, expectedExecutable))
        {
            issues.Add(Localization.T(mismatchResource));
        }
    }

    private async Task ShowStartupSelfCheckDetailsAsync()
    {
        var config = await _configService.LoadAsync();
        UpdateStartupSelfCheck(config, forceTaskRefresh: true);

        var status = _cachedStartupTasksStatus ?? TaskSchedulerInstaller.GetStatus();
        var expectedAgent = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Agent.exe");
        var expectedElevated = Path.Combine(AppContext.BaseDirectory, "AppWatcher.Elevated.exe");

        var issues = _startupSelfCheckIssues.Count == 0
            ? Localization.T("StartupSelfCheckNoIssues")
            : string.Join(
                Environment.NewLine,
                _startupSelfCheckIssues.Select(issue => $"• {issue}"));

        var taskDetails = string.Join(
            Environment.NewLine + Environment.NewLine,
            FormatStartupTaskStatus(
                Localization.T("StartupTaskAgentLabel"),
                status.Agent,
                expectedAgent),
            FormatStartupTaskStatus(
                Localization.T("StartupTaskElevatedLabel"),
                status.Elevated,
                expectedElevated));

        MessageBox.Show(
            this,
            Localization.F(
                "StartupSelfCheckDetailsFormat",
                issues,
                taskDetails),
            Localization.T("StartupSelfCheckTitle"),
            MessageBoxButtons.OK,
            _startupSelfCheckIssues.Count == 0
                ? MessageBoxIcon.Information
                : MessageBoxIcon.Warning);
    }

    private static string FormatStartupTaskStatus(
        string label,
        StartupTaskInfo info,
        string expectedPath)
    {
        if (!info.Installed)
        {
            return string.IsNullOrWhiteSpace(info.Error)
                ? Localization.F("StartupTaskNotInstalledFormat", label)
                : Localization.F("StartupTaskErrorFormat", label, info.Error);
        }

        var enabled = info.Enabled
            ? Localization.T("StartupTaskEnabled")
            : Localization.T("StartupTaskDisabled");
        var state = info.State switch
        {
            1 => Localization.T("StartupTaskStateDisabled"),
            2 => Localization.T("StartupTaskStateQueued"),
            3 => Localization.T("StartupTaskStateReady"),
            4 => Localization.T("StartupTaskStateRunning"),
            _ => Localization.T("StartupTaskStateUnknown")
        };

        var registeredPath = info.ExecutablePath ?? "-";
        var registeredWorkingDirectory = info.WorkingDirectory ?? "-";
        var registrationMatches = StartupTaskRegistrationMatches(info, expectedPath)
            ? Localization.T("StartupTaskPathMatches")
            : Localization.T("StartupTaskPathMismatch");

        return Localization.F(
            "StartupTaskInstalledFormat",
            label,
            enabled,
            state,
            registeredPath,
            registeredWorkingDirectory,
            registrationMatches,
            $"0x{info.LastTaskResult:X8}");
    }

    private static bool IsStartupTaskHealthy(
        StartupTaskInfo info,
        string expectedExecutable) =>
        info.Installed &&
        info.Enabled &&
        string.IsNullOrWhiteSpace(info.Error) &&
        StartupTaskRegistrationMatches(info, expectedExecutable);

    private static bool StartupTaskRegistrationMatches(
        StartupTaskInfo info,
        string expectedExecutable) =>
        PathsEqual(info.ExecutablePath ?? string.Empty, expectedExecutable) &&
        PathsEqual(info.WorkingDirectory ?? string.Empty, AppContext.BaseDirectory);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(left.Trim().Trim('"'))),
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(right.Trim().Trim('"'))),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(
                left.Trim().Trim('"').TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                right.Trim().Trim('"').TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task EnsureNormalAgentStartedAsync()
    {
        var ping = await _normalClient.SendAsync(new SupervisorRequest(SupervisorCommandType.Ping), TimeSpan.FromMilliseconds(350));
        if (ping.Success) return;
        StartHost("AppWatcher.Agent.exe", elevated: false, showErrors: false);
        await Task.Delay(500);
    }

    private bool StartHost(string fileName, bool elevated, bool showErrors = true)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            if (showErrors)
            {
                MessageBox.Show(this, Localization.F("HostExecutableNotFound", path), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return false;
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
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC cancelled.
            return false;
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
    }

    private async Task EnsureConfiguredHostsStartedAsync()
    {
        var config = await _configService.LoadAsync();
        if (!config.Applications.Any(a => a.Privilege == PrivilegeLevel.Administrator && a.MonitoringEnabled))
        {
            return;
        }

        var ping = await _adminClient.SendAsync(
            new SupervisorRequest(SupervisorCommandType.Ping),
            TimeSpan.FromMilliseconds(350));
        if (ping.Success) return;

        // Do not show UAC just because the dashboard was opened. If startup tasks
        // are installed, however, we can start the elevated helper silently.
        if (TaskSchedulerInstaller.TryStartRegisteredHost(PrivilegeLevel.Administrator))
        {
            await WaitForHostAsync(_adminClient, TimeSpan.FromSeconds(3));
        }
    }

    private async Task EnsureHostForDefinitionAsync(ApplicationDefinition definition)
    {
        var client = definition.Privilege == PrivilegeLevel.Administrator ? _adminClient : _normalClient;
        var ping = await client.SendAsync(
            new SupervisorRequest(SupervisorCommandType.Ping),
            TimeSpan.FromMilliseconds(350));
        if (ping.Success) return;

        var started = TaskSchedulerInstaller.TryStartRegisteredHost(definition.Privilege);
        if (started && await WaitForHostAsync(client, TimeSpan.FromSeconds(3)))
        {
            return;
        }

        started = definition.Privilege == PrivilegeLevel.Administrator
            ? StartHost("AppWatcher.Elevated.exe", elevated: true)
            : StartHost("AppWatcher.Agent.exe", elevated: false);

        if (started && await WaitForHostAsync(client, TimeSpan.FromSeconds(4)))
        {
            return;
        }

        if (definition.Privilege == PrivilegeLevel.Administrator)
        {
            var diagnostic = await _adminClient.SendAsync(
                new SupervisorRequest(SupervisorCommandType.Ping),
                TimeSpan.FromMilliseconds(500));

            MessageBox.Show(
                this,
                Localization.F(
                    "AdminHelperStartFailed",
                    definition.Name,
                    diagnostic.Error ?? Localization.T("ElevatedOffline")),
                "AppWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static async Task<bool> WaitForHostAsync(SupervisorClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ping = await client.SendAsync(
                new SupervisorRequest(SupervisorCommandType.Ping),
                TimeSpan.FromMilliseconds(300));
            if (ping.Success) return true;
            await Task.Delay(250);
        }
        return false;
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

            var config = await _configService.LoadAsync();
            var snapshots = new Dictionary<Guid, ApplicationSnapshot>();
            if (_normalHost is not null)
            {
                foreach (var app in _normalHost.Applications) snapshots[app.Id] = app;
            }
            if (_adminHost is not null)
            {
                foreach (var app in _adminHost.Applications) snapshots[app.Id] = app;
            }

            // The configuration is the dashboard's source of truth. This keeps an
            // administrator application visible even when Elevated Helper is offline,
            // instead of making a successfully saved application appear to vanish.
            var apps = new List<ApplicationSnapshot>();
            foreach (var definition in config.Applications)
            {
                apps.Add(snapshots.Remove(definition.Id, out var snapshot)
                    ? snapshot
                    : CreateUnavailableSnapshot(definition));
            }

            // Preserve visibility of a transient host snapshot until the configuration
            // catches up (for example, while a save/reload is in flight).
            apps.AddRange(snapshots.Values);

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
            var problems = apps.Count(a => a.State is AppRuntimeState.Failed or AppRuntimeState.Backoff or AppRuntimeState.Unresponsive or AppRuntimeState.Unknown);
            _summary.Text = Localization.F("SummaryFormat", healthy, paused, problems, apps.Count);
            UpdateHostStatus(normal.Error, admin.Error);
            UpdateStartupSelfCheck(config);
            UpdateCommandAvailability();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private ApplicationSnapshot CreateUnavailableSnapshot(ApplicationDefinition definition)
    {
        var hostOnline = definition.Privilege == PrivilegeLevel.Administrator
            ? _adminHost is not null
            : _normalHost is not null;

        var eventCode = hostOnline ? "ConfigurationPending" : "HostUnavailable";
        var reasonCode = hostOnline
            ? "ConfigurationReloadPending"
            : definition.Privilege == PrivilegeLevel.Administrator
                ? "ElevatedHelperOffline"
                : "AgentOffline";

        return new ApplicationSnapshot(
            definition.Id,
            definition.Name,
            definition.Privilege,
            AppRuntimeState.Unknown,
            null,
            null,
            null,
            0,
            null,
            false,
            eventCode,
            reasonCode,
            definition.ExecutablePath,
            definition.MonitoringEnabled,
            definition.DetectHangs);
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
            AppRuntimeState.Unknown => Color.DarkGoldenrod,
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

    private Task AddAsync() =>
        AddDefinitionAsync(new ApplicationDefinition());

    private async Task AddRunningApplicationAsync()
    {
        var response = await QueryRunningProcessesAsync();
        if (response is null)
        {
            return;
        }

        var config = await _configService.LoadAsync();
        using var picker = new RunningApplicationsForm(
            response,
            config.Applications.Select(application => application.ExecutablePath));

        if (picker.ShowDialog(this) != DialogResult.OK ||
            picker.SelectedProcess is null)
        {
            return;
        }

        var process = picker.SelectedProcess;
        string workingDirectory;
        try
        {
            workingDirectory = Path.GetDirectoryName(process.ExecutablePath) ?? string.Empty;
        }
        catch
        {
            workingDirectory = string.Empty;
        }

        var definition = new ApplicationDefinition
        {
            Name = process.Name,
            ExecutablePath = process.ExecutablePath,
            WorkingDirectory = workingDirectory,
            Privilege = process.Privilege,
            AttachExisting = true
        };

        await AddDefinitionAsync(definition);
    }

    private async Task<IReadOnlyList<RunningProcessInfo>?> QueryRunningProcessesAsync()
    {
        var response = await _adminClient.SendAsync(
            new SupervisorRequest(SupervisorCommandType.GetRunningProcesses),
            TimeSpan.FromSeconds(2));

        if (!response.Success)
        {
            if (TaskSchedulerInstaller.TryStartRegisteredHost(PrivilegeLevel.Administrator) &&
                await WaitForHostAsync(_adminClient, TimeSpan.FromSeconds(3)))
            {
                response = await _adminClient.SendAsync(
                    new SupervisorRequest(SupervisorCommandType.GetRunningProcesses),
                    TimeSpan.FromSeconds(3));
            }
        }

        if (!response.Success)
        {
            var start = MessageBox.Show(
                this,
                Localization.T("RunningAppsElevatedPrompt"),
                Localization.T("RunningAppsElevatedPromptTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);

            if (start != DialogResult.Yes)
            {
                return null;
            }

            if (StartHost("AppWatcher.Elevated.exe", elevated: true) &&
                await WaitForHostAsync(_adminClient, TimeSpan.FromSeconds(5)))
            {
                response = await _adminClient.SendAsync(
                    new SupervisorRequest(SupervisorCommandType.GetRunningProcesses),
                    TimeSpan.FromSeconds(3));
            }
        }

        if (!response.Success || response.RunningProcesses is null)
        {
            MessageBox.Show(
                this,
                Localization.F(
                    "RunningAppsQueryFailed",
                    response.Error ?? Localization.T("ElevatedOffline")),
                Localization.T("RunningAppsTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        return response.RunningProcesses;
    }

    private async Task AddDefinitionAsync(ApplicationDefinition definition)
    {
        using var editor = new AppEditForm(definition);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        var config = await _configService.LoadAsync();
        var duplicate = FindDuplicateApplication(config.Applications, editor.Result);
        if (duplicate is not null)
        {
            MessageBox.Show(
                this,
                Localization.F("DuplicateApplicationPrompt", duplicate.Name, duplicate.ExecutablePath),
                Localization.T("DuplicateApplicationTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        config.Applications.Add(editor.Result);
        if (!await ValidateBeforeSaveAsync(config, editor.Result)) return;
        if (!await TryUpdateConfigurationAsync(latest =>
        {
            if (FindDuplicateApplication(latest.Applications, editor.Result) is not null) throw new InvalidOperationException(Localization.T("DuplicateApplicationTitle"));
            latest.Applications.Add(editor.Result);
        })) return;
        await EnsureHostForDefinitionAsync(editor.Result);
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

        var duplicate = FindDuplicateApplication(config.Applications, editor.Result);
        if (duplicate is not null)
        {
            MessageBox.Show(
                this,
                Localization.F("DuplicateApplicationPrompt", duplicate.Name, duplicate.ExecutablePath),
                Localization.T("DuplicateApplicationTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var index = config.Applications.FindIndex(a => a.Id == selected.Id);
        if (index >= 0) config.Applications[index] = editor.Result;
        if (ApplicationSupervisor.IdentityChanged(existing, editor.Result))
        {
            var response = await (existing.Privilege == PrivilegeLevel.Normal ? _normalClient : _adminClient).SendAsync(new SupervisorRequest(SupervisorCommandType.GetSnapshot));
            var live = response.Snapshot?.Applications.FirstOrDefault(a => a.Id == existing.Id);
            if (live is null || live.ProcessId is not null || live.LastReason != "IntentionalStop")
            {
                MessageBox.Show(this, Localization.T("StopBeforeIdentityChange"), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (!await ValidateBeforeSaveAsync(config, editor.Result)) return;
        if (!await TryUpdateConfigurationAsync(latest =>
        {
            var currentIndex = latest.Applications.FindIndex(a => a.Id == selected.Id);
            if (currentIndex < 0) throw new InvalidOperationException("Application was removed while editing.");
            if (FindDuplicateApplication(latest.Applications, editor.Result) is not null) throw new InvalidOperationException(Localization.T("DuplicateApplicationTitle"));
            latest.Applications[currentIndex] = editor.Result;
        })) return;
        await EnsureHostForDefinitionAsync(editor.Result);
        await ReloadHostsAsync();
    }

    private static ApplicationDefinition? FindDuplicateApplication(
        IEnumerable<ApplicationDefinition> applications,
        ApplicationDefinition candidate)
    {
        var candidatePath = NormalizeExecutablePath(candidate.ExecutablePath);
        if (candidatePath.Length == 0) return null;

        return applications.FirstOrDefault(existing =>
            existing.Id != candidate.Id &&
            string.Equals(
                NormalizeExecutablePath(existing.ExecutablePath),
                candidatePath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Trim('"');
        }
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
        if (!await TryUpdateConfigurationAsync(latest => latest.Applications.RemoveAll(a => a.Id == selected.Id))) return;
        await ReloadHostsAsync();
    }

    private async Task<bool> TryUpdateConfigurationAsync(Action<AppWatcherConfiguration> update)
    {
        try { await _configService.UpdateAsync(update); return true; }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.F("ConfigurationSaveFailed", ex.Message), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private async Task ExecuteHostsAsync(SupervisorRequest request)
    {
        var config = await _configService.LoadAsync();
        var errors = await HostOperations.ExecuteAsync(config, request);
        if (errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, errors), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task<bool> ValidateBeforeSaveAsync(AppWatcherConfiguration next, ApplicationDefinition definition)
    {
        // 移管先を旧設定で先に起動し、権限変更を新規自動起動と誤認させない。
        await EnsureHostForDefinitionAsync(definition);
        var errors = await HostOperations.ExecuteAsync(next, new SupervisorRequest(SupervisorCommandType.ReloadConfiguration, ConfigurationPreview: next));
        if (errors.Count == 0) return true;
        MessageBox.Show(this, string.Join(Environment.NewLine, errors), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private async Task ReloadHostsAsync()
    {
        await ExecuteHostsAsync(new SupervisorRequest(SupervisorCommandType.ReloadConfiguration));
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
        await ExecuteHostsAsync(new SupervisorRequest(SupervisorCommandType.StartMaintenance, DurationSeconds: seconds));
        await RefreshDashboardAsync();
    }

    private async Task ResumeGlobalMaintenanceAsync()
    {
        await ExecuteHostsAsync(new SupervisorRequest(SupervisorCommandType.ResumeMaintenance));
        await RefreshDashboardAsync();
    }

    private async Task OpenSettingsAsync()
    {
        using var settings = new SettingsForm();
        settings.ShowDialog(this);
        await RefreshDashboardAsync();
    }


    private async Task RestartAppWatcherAsync()
    {
        if (MessageBox.Show(this,
                Localization.T("RestartAppWatcherPrompt"),
                Localization.T("RestartAppWatcherTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        _refreshTimer.Stop();
        var success = await HostLifecycle.RestartHostsAsync();
        if (!success)
        {
            MessageBox.Show(this,
                Localization.T("RestartAppWatcherFailed"),
                Localization.T("RestartAppWatcherTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        _refreshTimer.Start();
        await RefreshDashboardAsync();
    }

    private async Task ExitAppWatcherAsync()
    {
        if (MessageBox.Show(this,
                Localization.T("ExitAppWatcherPrompt"),
                Localization.T("ExitAppWatcherTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        _refreshTimer.Stop();
        var success = await HostLifecycle.ShutdownAllAsync();
        if (!success)
        {
            MessageBox.Show(this,
                Localization.T("ExitAppWatcherFailed"),
                Localization.T("ExitAppWatcherTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _refreshTimer.Start();
            return;
        }

        Close();
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
