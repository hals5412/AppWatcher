using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class AppEditForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _args = new();
    private readonly TextBox _workingDir = new();
    private readonly ComboBox _privilege = new();
    private readonly CheckBox _monitoringEnabled = new() { Text = "Monitoring enabled" };
    private readonly CheckBox _startWithWatcher = new() { Text = "Start automatically when AppWatcher starts" };
    private readonly CheckBox _attachExisting = new() { Text = "Attach to an existing instance if found" };

    private readonly ComboBox _restartPolicy = new();
    private readonly NumericUpDown _restartDelay = Number(0, 3600);
    private readonly CheckBox _detectHangs = new() { Text = "Detect unresponsive GUI window" };
    private readonly NumericUpDown _hangTimeout = Number(5, 3600);
    private readonly NumericUpDown _hangInterval = Number(1, 300);
    private readonly NumericUpDown _startupGrace = Number(0, 3600);

    private readonly CheckBox _loopProtection = new() { Text = "Enable restart-loop protection" };
    private readonly NumericUpDown _maxRestarts = Number(1, 1000);
    private readonly NumericUpDown _restartWindow = Number(1, 1440);
    private readonly NumericUpDown _backoff = Number(1, 1440);
    private readonly NumericUpDown _healthyReset = Number(1, 1440);
    private readonly NumericUpDown _gracefulShutdown = Number(1, 300);
    private readonly CheckBox _forceKill = new() { Text = "Force terminate if graceful shutdown times out" };
    private readonly ComboBox _childPolicy = new();
    private readonly ComboBox _logLevel = new();

    private readonly Guid _id;
    public ApplicationDefinition Result { get; private set; }

    public AppEditForm(ApplicationDefinition definition)
    {
        _id = definition.Id;
        Result = definition;

        Text = definition.Name == "New application" ? "Add application" : $"Edit - {definition.Name}";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 650;
        MinimumSize = new Size(650, 560);

        _privilege.DropDownStyle = ComboBoxStyle.DropDownList;
        _privilege.Items.AddRange(Enum.GetNames<PrivilegeLevel>());
        _restartPolicy.DropDownStyle = ComboBoxStyle.DropDownList;
        _restartPolicy.Items.AddRange(Enum.GetNames<RestartPolicy>());
        _childPolicy.DropDownStyle = ComboBoxStyle.DropDownList;
        _childPolicy.Items.Add(ChildProcessPolicy.Unmanaged.ToString());
        _logLevel.DropDownStyle = ComboBoxStyle.DropDownList;
        _logLevel.Items.AddRange(Enum.GetNames<AppLogLevel>());

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildMonitoringTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        Controls.Add(tabs);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6)
        };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var validate = new Button { Text = "Validate", AutoSize = true };
        validate.Click += (_, _) => ValidateOnly();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(validate);
        Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
        LoadFrom(definition);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General");
        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, "Name", _name);

        var exePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 30 };
        exePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        _exe.Dock = DockStyle.Fill;
        var browseExe = new Button { Text = "...", Dock = DockStyle.Fill };
        browseExe.Click += (_, _) => BrowseExecutable();
        exePanel.Controls.Add(_exe, 0, 0);
        exePanel.Controls.Add(browseExe, 1, 0);
        AddRow(table, "Executable", exePanel);

        AddRow(table, "Arguments", _args);

        var wdPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 30 };
        wdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        wdPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        _workingDir.Dock = DockStyle.Fill;
        var browseWd = new Button { Text = "...", Dock = DockStyle.Fill };
        browseWd.Click += (_, _) => BrowseWorkingDirectory();
        wdPanel.Controls.Add(_workingDir, 0, 0);
        wdPanel.Controls.Add(browseWd, 1, 0);
        AddRow(table, "Working directory", wdPanel);

        AddRow(table, "Privilege", _privilege);
        AddFullRow(table, _monitoringEnabled);
        AddFullRow(table, _startWithWatcher);
        AddFullRow(table, _attachExisting);
        AddFullRow(table, new Label
        {
            Text = "Launch mode: Interactive desktop (normal Windows GUI behavior). AppWatcher does not place targets in Session 0 or a Job Object.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DimGray
        });
        return page;
    }

    private TabPage BuildMonitoringTab()
    {
        var page = new TabPage("Monitoring");
        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, "Restart policy", _restartPolicy);
        AddRow(table, "Restart delay (sec)", _restartDelay);
        AddFullRow(table, _detectHangs);
        AddRow(table, "Hang timeout (sec)", _hangTimeout);
        AddRow(table, "Hang check interval (sec)", _hangInterval);
        AddRow(table, "Startup grace period (sec)", _startupGrace);
        AddFullRow(table, _loopProtection);
        AddRow(table, "Max restarts", _maxRestarts);
        AddRow(table, "Within (minutes)", _restartWindow);
        AddRow(table, "Backoff (minutes)", _backoff);
        AddRow(table, "Healthy reset (minutes)", _healthyReset);
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage("Advanced");
        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, "Graceful shutdown timeout", _gracefulShutdown);
        AddFullRow(table, _forceKill);
        AddRow(table, "Child process policy", _childPolicy);
        AddRow(table, "Log level", _logLevel);
        AddFullRow(table, new Label
        {
            Text = "Recommended child-process policy: Unmanaged. This preserves compatibility with applications such as TVRock that start TVTest or other interactive GUI processes.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DimGray
        });
        return page;
    }

    private static TableLayoutPanel CreateTable() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        ColumnCount = 2,
        Padding = new Padding(12),
        AutoSize = false,
        ColumnStyles =
        {
            new ColumnStyle(SizeType.Absolute, 190),
            new ColumnStyle(SizeType.Percent, 100)
        }
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) };
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 5, 3, 5);
        if (control is TextBox or ComboBox) control.Width = 430;
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static void AddFullRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(3, 8, 3, 8);
        table.Controls.Add(control, 1, row);
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 120
    };

    private void LoadFrom(ApplicationDefinition d)
    {
        _name.Text = d.Name;
        _exe.Text = d.ExecutablePath;
        _args.Text = d.Arguments;
        _workingDir.Text = d.WorkingDirectory;
        _privilege.SelectedItem = d.Privilege.ToString();
        _monitoringEnabled.Checked = d.MonitoringEnabled;
        _startWithWatcher.Checked = d.StartWithWatcher;
        _attachExisting.Checked = d.AttachExisting;

        _restartPolicy.SelectedItem = d.RestartPolicy.ToString();
        _restartDelay.Value = Clamp(_restartDelay, d.RestartDelaySeconds);
        _detectHangs.Checked = d.DetectHangs;
        _hangTimeout.Value = Clamp(_hangTimeout, d.HangTimeoutSeconds);
        _hangInterval.Value = Clamp(_hangInterval, d.HangCheckIntervalSeconds);
        _startupGrace.Value = Clamp(_startupGrace, d.StartupGraceSeconds);
        _loopProtection.Checked = d.RestartLoopProtectionEnabled;
        _maxRestarts.Value = Clamp(_maxRestarts, d.MaxRestarts);
        _restartWindow.Value = Clamp(_restartWindow, d.RestartWindowMinutes);
        _backoff.Value = Clamp(_backoff, d.BackoffMinutes);
        _healthyReset.Value = Clamp(_healthyReset, d.HealthyResetMinutes);
        _gracefulShutdown.Value = Clamp(_gracefulShutdown, d.GracefulShutdownSeconds);
        _forceKill.Checked = d.ForceKillAfterTimeout;
        _childPolicy.SelectedItem = d.ChildProcessPolicy.ToString();
        _logLevel.SelectedItem = d.LogLevel.ToString();
    }

    private static decimal Clamp(NumericUpDown control, int value) => Math.Min(control.Maximum, Math.Max(control.Minimum, value));

    private ApplicationDefinition BuildResult() => new()
    {
        Id = _id,
        Name = _name.Text.Trim(),
        ExecutablePath = _exe.Text.Trim(),
        Arguments = _args.Text,
        WorkingDirectory = _workingDir.Text.Trim(),
        Privilege = ParseEnum(_privilege, PrivilegeLevel.Normal),
        MonitoringEnabled = _monitoringEnabled.Checked,
        StartWithWatcher = _startWithWatcher.Checked,
        AttachExisting = _attachExisting.Checked,
        RestartPolicy = ParseEnum(_restartPolicy, RestartPolicy.AnyUnexpectedExit),
        RestartDelaySeconds = (int)_restartDelay.Value,
        DetectHangs = _detectHangs.Checked,
        HangTimeoutSeconds = (int)_hangTimeout.Value,
        HangCheckIntervalSeconds = (int)_hangInterval.Value,
        StartupGraceSeconds = (int)_startupGrace.Value,
        RestartLoopProtectionEnabled = _loopProtection.Checked,
        MaxRestarts = (int)_maxRestarts.Value,
        RestartWindowMinutes = (int)_restartWindow.Value,
        BackoffMinutes = (int)_backoff.Value,
        HealthyResetMinutes = (int)_healthyReset.Value,
        GracefulShutdownSeconds = (int)_gracefulShutdown.Value,
        ForceKillAfterTimeout = _forceKill.Checked,
        ChildProcessPolicy = ParseEnum(_childPolicy, ChildProcessPolicy.Unmanaged),
        LogLevel = ParseEnum(_logLevel, AppLogLevel.Information)
    };

    private static T ParseEnum<T>(ComboBox box, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(box.SelectedItem?.ToString(), out var value) ? value : fallback;

    private void ValidateOnly()
    {
        var result = new ConfigService().Validate(BuildResult());
        MessageBox.Show(this,
            result.Success ? "Configuration is valid." : string.Join(Environment.NewLine, result.Messages),
            "Configuration validation",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void SaveAndClose()
    {
        var candidate = BuildResult();
        var validation = new ConfigService().Validate(candidate);
        if (!validation.Success)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, validation.Messages), "Cannot save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Title = "Select application"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _exe.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "New application")
        {
            _name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
        if (string.IsNullOrWhiteSpace(_workingDir.Text))
        {
            _workingDir.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select working directory", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingDir.Text = dialog.SelectedPath;
        }
    }
}
