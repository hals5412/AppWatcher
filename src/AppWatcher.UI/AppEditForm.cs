using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class AppEditForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _args = new();
    private readonly TextBox _workingDir = new();
    private readonly ComboBox _privilege = new();
    private readonly CheckBox _monitoringEnabled = new() { Text = Localization.T("MonitoringEnabled"), AutoSize = true };
    private readonly CheckBox _startWithWatcher = new() { Text = Localization.T("StartWithWatcher"), AutoSize = true };
    private readonly CheckBox _attachExisting = new() { Text = Localization.T("AttachExisting"), AutoSize = true };

    private readonly ComboBox _restartPolicy = new();
    private readonly NumericUpDown _restartDelay = Number(0, 3600);
    private readonly CheckBox _detectHangs = new() { Text = Localization.T("DetectUnresponsiveWindow"), AutoSize = true };
    private readonly NumericUpDown _hangTimeout = Number(5, 3600);
    private readonly NumericUpDown _hangInterval = Number(1, 300);
    private readonly NumericUpDown _startupGrace = Number(0, 3600);

    private readonly CheckBox _loopProtection = new() { Text = Localization.T("EnableRestartLoopProtection"), AutoSize = true };
    private readonly NumericUpDown _maxRestarts = Number(1, 1000);
    private readonly NumericUpDown _restartWindow = Number(1, 1440);
    private readonly NumericUpDown _backoff = Number(1, 1440);
    private readonly NumericUpDown _healthyReset = Number(1, 1440);
    private readonly NumericUpDown _gracefulShutdown = Number(1, 300);
    private readonly CheckBox _forceKill = new() { Text = Localization.T("ForceTerminateAfterTimeout"), AutoSize = true };
    private readonly ComboBox _childPolicy = new();
    private readonly ComboBox _logLevel = new();
    private readonly ToolTip _pathToolTip = new();

    private readonly Guid _id;
    public ApplicationDefinition Result { get; private set; }

    public AppEditForm(ApplicationDefinition definition)
    {
        _id = definition.Id;
        Result = definition;

        Text = string.IsNullOrWhiteSpace(definition.Name) || definition.Name == "New application"
            ? Localization.T("TitleAddApplication")
            : Localization.F("TitleEditApplication", definition.Name);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 980;
        Height = 720;
        MinimumSize = new Size(860, 620);

        BindEnum(_privilege, Enum.GetValues<PrivilegeLevel>(), Localization.PrivilegeText);
        BindEnum(_restartPolicy, Enum.GetValues<RestartPolicy>(), Localization.RestartPolicyText);
        BindEnum(_childPolicy, new[] { ChildProcessPolicy.Unmanaged }, Localization.ChildProcessPolicyText);
        BindEnum(_logLevel, Enum.GetValues<AppLogLevel>(), Localization.LogLevelText);

        _exe.TextChanged += (_, _) => _pathToolTip.SetToolTip(_exe, _exe.Text);
        _workingDir.TextChanged += (_, _) => _pathToolTip.SetToolTip(_workingDir, _workingDir.Text);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6)
        };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildMonitoringTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        Controls.Add(tabs);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            WrapContents = false
        };

        var save = new Button
        {
            Text = Localization.T("ButtonSave"),
            AutoSize = true,
            MinimumSize = new Size(80, 32)
        };
        save.Click += (_, _) => SaveAndClose();

        var cancel = new Button
        {
            Text = Localization.T("ButtonCancel"),
            AutoSize = true,
            MinimumSize = new Size(80, 32),
            DialogResult = DialogResult.Cancel
        };

        var validate = new Button
        {
            Text = Localization.T("ButtonValidate"),
            AutoSize = true,
            MinimumSize = new Size(96, 32)
        };
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
        var page = new TabPage(Localization.T("TabGeneral"))
        {
            Padding = new Padding(6)
        };

        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, Localization.T("FieldName"), _name);
        AddRow(table, Localization.T("FieldExecutable"), CreatePathPicker(_exe, BrowseExecutable));
        AddRow(table, Localization.T("FieldArguments"), _args);
        AddRow(table, Localization.T("FieldWorkingDirectory"), CreatePathPicker(_workingDir, BrowseWorkingDirectory));
        AddRow(table, Localization.T("FieldPrivilege"), _privilege);

        AddFullRow(table, _monitoringEnabled);
        AddFullRow(table, _startWithWatcher);
        AddFullRow(table, _attachExisting);
        AddFullRow(table, InfoLabel(Localization.T("InteractiveLaunchInfo")));

        return page;
    }

    private TabPage BuildMonitoringTab()
    {
        var page = new TabPage(Localization.T("TabMonitoring"))
        {
            Padding = new Padding(6)
        };

        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, Localization.T("FieldRestartPolicy"), _restartPolicy);
        AddRow(table, Localization.T("FieldRestartDelaySeconds"), _restartDelay);
        AddFullRow(table, _detectHangs);
        AddRow(table, Localization.T("FieldHangTimeoutSeconds"), _hangTimeout);
        AddRow(table, Localization.T("FieldHangCheckIntervalSeconds"), _hangInterval);
        AddRow(table, Localization.T("FieldStartupGraceSeconds"), _startupGrace);
        AddFullRow(table, _loopProtection);
        AddRow(table, Localization.T("FieldMaxRestarts"), _maxRestarts);
        AddRow(table, Localization.T("FieldWithinMinutes"), _restartWindow);
        AddRow(table, Localization.T("FieldBackoffMinutes"), _backoff);
        AddRow(table, Localization.T("FieldHealthyResetMinutes"), _healthyReset);

        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage(Localization.T("TabAdvanced"))
        {
            Padding = new Padding(6)
        };

        var table = CreateTable();
        page.Controls.Add(table);

        AddRow(table, Localization.T("FieldGracefulShutdownTimeout"), _gracefulShutdown);
        AddFullRow(table, _forceKill);
        AddRow(table, Localization.T("FieldChildProcessPolicy"), _childPolicy);
        AddRow(table, Localization.T("FieldLogLevel"), _logLevel);
        AddFullRow(table, InfoLabel(Localization.T("ChildProcessPolicyInfo")));

        return page;
    }

    private static TableLayoutPanel CreatePathPicker(TextBox textBox, Action browseAction)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 34,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 3, 6, 3);

        var browse = new Button
        {
            Text = "...",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2)
        };
        browse.Click += (_, _) => browseAction();

        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browse, 1, 0);

        return panel;
    }

    private static Label InfoLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.DimGray,
        Margin = new Padding(3, 12, 3, 6)
    };

    private static TableLayoutPanel CreateTable() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        ColumnCount = 2,
        Padding = new Padding(16, 14, 16, 14),
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

        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 12, 8)
        };

        control.Margin = new Padding(3, 4, 3, 4);

        if (control is NumericUpDown)
        {
            control.Anchor = AnchorStyles.Left;
        }
        else
        {
            control.Dock = DockStyle.Fill;
        }

        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static void AddFullRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        control.Margin = new Padding(3, 8, 3, 8);

        if (control is CheckBox checkBox)
        {
            checkBox.AutoSize = true;
            checkBox.Anchor = AnchorStyles.Left;
        }
        else if (control is Label label)
        {
            label.AutoSize = true;
            label.MaximumSize = new Size(0, 0);
            label.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        else
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }

        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 140
    };

    private void LoadFrom(ApplicationDefinition d)
    {
        _name.Text = d.Name;
        _exe.Text = d.ExecutablePath;
        _args.Text = d.Arguments;
        _workingDir.Text = d.WorkingDirectory;
        SelectEnum(_privilege, d.Privilege);
        _monitoringEnabled.Checked = d.MonitoringEnabled;
        _startWithWatcher.Checked = d.StartWithWatcher;
        _attachExisting.Checked = d.AttachExisting;

        SelectEnum(_restartPolicy, d.RestartPolicy);
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
        SelectEnum(_childPolicy, d.ChildProcessPolicy);
        SelectEnum(_logLevel, d.LogLevel);
    }

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Min(control.Maximum, Math.Max(control.Minimum, value));

    private ApplicationDefinition BuildResult() => new()
    {
        Id = _id,
        Name = _name.Text.Trim(),
        ExecutablePath = _exe.Text.Trim(),
        Arguments = _args.Text,
        WorkingDirectory = _workingDir.Text.Trim(),
        Privilege = SelectedEnum(_privilege, PrivilegeLevel.Normal),
        MonitoringEnabled = _monitoringEnabled.Checked,
        StartWithWatcher = _startWithWatcher.Checked,
        AttachExisting = _attachExisting.Checked,
        RestartPolicy = SelectedEnum(_restartPolicy, RestartPolicy.AnyUnexpectedExit),
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
        ChildProcessPolicy = SelectedEnum(_childPolicy, ChildProcessPolicy.Unmanaged),
        LogLevel = SelectedEnum(_logLevel, AppLogLevel.Information)
    };

    private static void BindEnum<T>(ComboBox box, IEnumerable<T> values, Func<T, string> text) where T : struct, Enum
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var value in values)
        {
            box.Items.Add(new LocalizedOption<T>(value, text(value)));
        }
    }

    private static void SelectEnum<T>(ComboBox box, T value) where T : struct, Enum
    {
        foreach (var item in box.Items.OfType<LocalizedOption<T>>())
        {
            if (EqualityComparer<T>.Default.Equals(item.Value, value))
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static T SelectedEnum<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is LocalizedOption<T> item ? item.Value : fallback;

    private void ValidateOnly()
    {
        var result = new ConfigService().Validate(BuildResult());
        MessageBox.Show(
            this,
            result.Success ? Localization.T("ConfigurationValid") : string.Join(Environment.NewLine, result.Messages),
            Localization.T("ConfigurationValidation"),
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void SaveAndClose()
    {
        var candidate = BuildResult();
        var validation = new ConfigService().Validate(candidate);
        if (!validation.Success)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, validation.Messages),
                Localization.T("CannotSave"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
            Filter = Localization.T("ApplicationsFilter"),
            CheckFileExists = true,
            Title = Localization.T("SelectApplication")
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
        using var dialog = new FolderBrowserDialog
        {
            Description = Localization.T("SelectWorkingDirectory"),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingDir.Text = dialog.SelectedPath;
        }
    }
}
