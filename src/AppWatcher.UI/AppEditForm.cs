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
    private readonly ComboBox _hangAction = new();
    private readonly NumericUpDown _startupGrace = Number(0, 3600);

    private readonly CheckBox _loopProtection = new() { Text = Localization.T("EnableRestartLoopProtection"), AutoSize = true };
    private readonly NumericUpDown _maxRestarts = Number(1, 1000);
    private readonly NumericUpDown _restartWindow = Number(1, 1440);
    private readonly NumericUpDown _backoff = Number(1, 1440);
    private readonly NumericUpDown _healthyReset = Number(1, 1440);
    private readonly NumericUpDown _gracefulShutdown = Number(1, 300);
    private readonly CheckBox _forceKill = new() { Text = Localization.T("ForceTerminateAfterTimeout"), AutoSize = true };
    private readonly ComboBox _logLevel = new();

    private readonly Label _restartSummary = WrappingLabel();
    private readonly Label _zeroDelayWarning = WrappingLabel(Color.DarkRed);
    private readonly Label _loopSummary = WrappingLabel();
    private readonly Label _errorSummary = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DarkRed,
        AutoEllipsis = true
    };
    private readonly Button _browseExe = BrowseButton();
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly ToolTip _toolTip = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
    private readonly List<(Control Control, string Message)> _currentErrors = [];

    private readonly Guid _id;
    private readonly ChildProcessPolicy _childPolicy;
    private readonly Func<ApplicationDefinition, string?>? _saveCheck;
    private Func<Task<string?>>? _stopForIdentityChange;
    private Panel? _identityNotice;

    public ApplicationDefinition Result { get; private set; }

    // saveCheck: 保存前の追加チェック（重複登録など）。エラー文を返すと画面を閉じずに表示する。
    // stopForIdentityChange: 動作中で実行ファイル・権限を変更できない場合に渡す停止処理。
    public AppEditForm(
        ApplicationDefinition definition,
        Func<ApplicationDefinition, string?>? saveCheck = null,
        Func<Task<string?>>? stopForIdentityChange = null)
    {
        _id = definition.Id;
        _childPolicy = definition.ChildProcessPolicy;
        _saveCheck = saveCheck;
        _stopForIdentityChange = stopForIdentityChange;
        Result = definition;

        Text = string.IsNullOrWhiteSpace(definition.Name) || definition.Name == "New application"
            ? Localization.T("TitleAddApplication")
            : Localization.F("TitleEditApplication", definition.Name);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 940;
        Height = 640;
        // 監視タブは2列の枠が並ぶため、これより小さいと下端の説明文が切れる。
        MinimumSize = new Size(820, 640);

        BindEnum(_privilege, Enum.GetValues<PrivilegeLevel>(), Localization.PrivilegeText);
        BindEnum(_restartPolicy, Enum.GetValues<RestartPolicy>(), Localization.RestartPolicyText);
        BindEnum(_hangAction, Enum.GetValues<HangAction>(), Localization.HangActionText);
        BindEnum(_logLevel, Enum.GetValues<AppLogLevel>(), Localization.LogLevelText);

        _exe.TextChanged += (_, _) => _toolTip.SetToolTip(_exe, _exe.Text);
        _workingDir.TextChanged += (_, _) => _toolTip.SetToolTip(_workingDir, _workingDir.Text);
        _browseExe.Click += (_, _) => BrowseExecutable();

        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildMonitoringTab());
        _tabs.TabPages.Add(BuildAdvancedTab());
        Controls.Add(_tabs);
        Controls.Add(BuildButtonBar());

        LoadFrom(definition);
        WireLiveUpdates();
        UpdateDependentStates();
        ValidateInputs();
    }

    private TabPage BuildGeneralTab()
    {
        var application = Group(Localization.T("GroupApplication"), out var appTable);
        AddRow(appTable, Localization.T("FieldName"), _name, fill: true);
        AddRow(appTable, Localization.T("FieldExecutable"), PathPicker(_exe, _browseExe), fill: true);
        AddRow(appTable, Localization.T("FieldArguments"), _args, fill: true);
        AddRow(appTable, Localization.T("FieldWorkingDirectory"), PathPicker(_workingDir, BrowseButton(BrowseWorkingDirectory)), fill: true);
        AddRow(appTable, Localization.T("FieldPrivilege"), _privilege, fill: true);
        if (_stopForIdentityChange is not null)
        {
            _identityNotice = BuildIdentityNotice();
            AddSpanning(appTable, _identityNotice);
        }

        var startup = Group(Localization.T("GroupStartup"), out var startupTable);
        AddSpanning(startupTable, _monitoringEnabled);
        AddSpanning(startupTable, _startWithWatcher);
        AddSpanning(startupTable, _attachExisting);

        return Page(Localization.T("TabGeneral"), [application], [startup]);
    }

    private TabPage BuildMonitoringTab()
    {
        var restart = Group(Localization.T("GroupRestart"), out var restartTable);
        AddRow(restartTable, Localization.T("FieldRestartPolicy"), _restartPolicy, fill: true);
        AddRow(restartTable, Localization.T("FieldRestartDelaySeconds"), _restartDelay);
        AddSpanning(restartTable, _restartSummary);
        AddSpanning(restartTable, _zeroDelayWarning);

        var hang = Group(Localization.T("GroupHangDetection"), out var hangTable);
        AddSpanning(hangTable, _detectHangs);
        AddRow(hangTable, Localization.T("FieldHangTimeoutSeconds"), _hangTimeout);
        AddRow(hangTable, Localization.T("FieldHangCheckIntervalSeconds"), _hangInterval);
        AddRow(hangTable, Localization.T("FieldStartupGraceSeconds"), _startupGrace);
        AddStacked(hangTable, Localization.T("FieldHangAction"), _hangAction);

        var loop = Group(Localization.T("GroupLoopProtection"), out var loopTable);
        AddSpanning(loopTable, _loopProtection);
        AddRow(loopTable, Localization.T("FieldMaxRestarts"), _maxRestarts);
        AddRow(loopTable, Localization.T("FieldWithinMinutes"), _restartWindow);
        AddRow(loopTable, Localization.T("FieldBackoffMinutes"), _backoff);
        AddRow(loopTable, Localization.T("FieldHealthyResetMinutes"), _healthyReset);
        AddSpanning(loopTable, _loopSummary);

        // 応答なし検出とループ防止は横に並べ、縦に長くならないようにする。
        return Page(Localization.T("TabMonitoring"), [restart], [hang, loop]);
    }

    private TabPage BuildAdvancedTab()
    {
        var stop = Group(Localization.T("GroupStopOperation"), out var stopTable);
        AddRow(stopTable, Localization.T("FieldGracefulShutdownTimeout"), _gracefulShutdown);
        AddSpanning(stopTable, _forceKill);
        AddSpanning(stopTable, WrappingLabel(text: Localization.T("StopOperationInfo")));

        var logging = Group(Localization.T("GroupLogging"), out var loggingTable);
        AddRow(loggingTable, Localization.T("FieldLogLevel"), _logLevel, fill: true);

        var child = Group(Localization.T("GroupChildProcesses"), out var childTable);
        AddSpanning(childTable, WrappingLabel(text: Localization.T("ChildProcessPolicyInfo")));

        return Page(Localization.T("TabAdvanced"), [stop], [logging], [child]);
    }

    private Control BuildButtonBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            ColumnCount = 3,
            Padding = new Padding(12, 8, 8, 8)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var save = new Button { Text = Localization.T("ButtonSave"), AutoSize = true, MinimumSize = new Size(88, 32) };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = Localization.T("ButtonCancel"),
            AutoSize = true,
            MinimumSize = new Size(88, 32),
            DialogResult = DialogResult.Cancel
        };

        bar.Controls.Add(_errorSummary, 0, 0);
        bar.Controls.Add(cancel, 1, 0);
        bar.Controls.Add(save, 2, 0);
        AcceptButton = save;
        CancelButton = cancel;
        return bar;
    }

    private Panel BuildIdentityNotice()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        var text = new Label
        {
            Text = Localization.T("IdentityLockedInfo"),
            AutoSize = true,
            ForeColor = Color.DarkGoldenrod,
            Margin = new Padding(3, 9, 12, 3)
        };
        var stop = new Button { Text = Localization.T("ButtonStopToEdit"), AutoSize = true, MinimumSize = new Size(0, 30) };
        stop.Click += async (_, _) => await StopForIdentityChangeAsync(stop);
        panel.Controls.Add(text);
        panel.Controls.Add(stop);
        return panel;
    }

    private void WireLiveUpdates()
    {
        EventHandler changed = (_, _) =>
        {
            UpdateDependentStates();
            ValidateInputs();
        };
        foreach (var box in new[] { _name, _exe, _workingDir }) box.TextChanged += changed;
        foreach (var check in new[] { _detectHangs, _loopProtection }) check.CheckedChanged += changed;
        _restartPolicy.SelectedIndexChanged += changed;
        foreach (var number in new[] { _restartDelay, _hangTimeout, _hangInterval, _maxRestarts, _restartWindow, _backoff, _healthyReset })
            number.ValueChanged += changed;
    }

    // 使われない設定はグレーアウトし、どの設定が効いているかを見て分かるようにする。
    private void UpdateDependentStates()
    {
        var restartEnabled = SelectedEnum(_restartPolicy, RestartPolicy.AnyUnexpectedExit) != RestartPolicy.Never;
        _restartDelay.Enabled = restartEnabled;
        _loopProtection.Enabled = restartEnabled;
        var limitEnabled = restartEnabled && _loopProtection.Checked;
        foreach (var control in new Control[] { _maxRestarts, _restartWindow, _backoff, _healthyReset }) control.Enabled = limitEnabled;

        var hangEnabled = _detectHangs.Checked;
        foreach (var control in new Control[] { _hangTimeout, _hangInterval, _startupGrace, _hangAction }) control.Enabled = hangEnabled;

        _restartSummary.Text = restartEnabled ? Localization.T("RestartPolicyInfo") : Localization.T("RestartDisabledSummary");
        _loopSummary.Text = !restartEnabled
            ? Localization.T("RestartDisabledSummary")
            : limitEnabled
                ? Localization.F("LoopProtectionSummary", (int)_maxRestarts.Value, (int)_restartWindow.Value, (int)_backoff.Value, (int)_healthyReset.Value)
                : Localization.T("LoopProtectionDisabledSummary");
        _zeroDelayWarning.Text = Localization.T("ZeroDelayWarning");
        _zeroDelayWarning.Visible = IsZeroDelayWithoutLimit();
    }

    private bool IsZeroDelayWithoutLimit() =>
        SelectedEnum(_restartPolicy, RestartPolicy.AnyUnexpectedExit) != RestartPolicy.Never &&
        _restartDelay.Value == 0 &&
        !_loopProtection.Checked;

    // 入力の誤りは、保存を押す前にその項目の横へ表示する。
    private bool ValidateInputs()
    {
        _currentErrors.Clear();
        void Check(bool invalid, Control control, string message)
        {
            if (invalid) _currentErrors.Add((control, message));
            _errors.SetError(control, invalid ? message : string.Empty);
        }

        var exe = _exe.Text.Trim();
        var workingDirectory = _workingDir.Text.Trim();
        Check(string.IsNullOrWhiteSpace(_name.Text), _name, Localization.T("ValidationApplicationNameRequired"));
        Check(exe.Length == 0, _exe, Localization.T("ValidationExecutablePathRequired"));
        if (exe.Length > 0) Check(!File.Exists(exe), _exe, Localization.F("ValidationExecutableMissing", exe));
        Check(workingDirectory.Length > 0 && !Directory.Exists(workingDirectory), _workingDir, Localization.F("ValidationWorkingDirectoryMissing", workingDirectory));
        Check(_detectHangs.Checked && _hangTimeout.Value < _hangInterval.Value, _hangTimeout, Localization.T("ValidationHangTimeout"));

        _errorSummary.Text = _currentErrors.Count == 0
            ? string.Empty
            : Localization.F("ValidationSummary", _currentErrors[0].Message);
        return _currentErrors.Count == 0;
    }

    private void SaveAndClose()
    {
        if (!ValidateInputs())
        {
            FocusControl(_currentErrors[0].Control);
            return;
        }

        var candidate = BuildResult();
        var validation = new ConfigService().Validate(candidate);
        var extra = validation.Success ? _saveCheck?.Invoke(candidate) : string.Join(Environment.NewLine, validation.Messages);
        if (extra is not null)
        {
            MessageBox.Show(this, extra, Localization.T("CannotSave"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (IsZeroDelayWithoutLimit() &&
            MessageBox.Show(this, Localization.T("ZeroDelayConfirm"), "AppWatcher", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            FocusControl(_restartDelay);
            return;
        }

        Result = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task StopForIdentityChangeAsync(Button button)
    {
        if (_stopForIdentityChange is null) return;
        if (MessageBox.Show(this, Localization.F("StopToEditConfirm", _name.Text), "AppWatcher", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        button.Enabled = false;
        var error = await _stopForIdentityChange();
        if (error is not null)
        {
            button.Enabled = true;
            MessageBox.Show(this, Localization.F("StopToEditFailed", error), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _stopForIdentityChange = null;
        if (_identityNotice is not null) _identityNotice.Visible = false;
        SetIdentityLocked(false);
    }

    private void SetIdentityLocked(bool locked)
    {
        _exe.ReadOnly = locked;
        _browseExe.Enabled = !locked;
        _privilege.Enabled = !locked;
    }

    private void FocusControl(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is TabPage page)
            {
                _tabs.SelectedTab = page;
                break;
            }
        }
        control.Focus();
    }

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
        SelectEnum(_hangAction, d.HangAction);
        _startupGrace.Value = Clamp(_startupGrace, d.StartupGraceSeconds);
        _loopProtection.Checked = d.RestartLoopProtectionEnabled;
        _maxRestarts.Value = Clamp(_maxRestarts, d.MaxRestarts);
        _restartWindow.Value = Clamp(_restartWindow, d.RestartWindowMinutes);
        _backoff.Value = Clamp(_backoff, d.BackoffMinutes);
        _healthyReset.Value = Clamp(_healthyReset, d.HealthyResetMinutes);
        _gracefulShutdown.Value = Clamp(_gracefulShutdown, d.GracefulShutdownSeconds);
        _forceKill.Checked = d.ForceKillAfterTimeout;
        SelectEnum(_logLevel, d.LogLevel);
        SetIdentityLocked(_stopForIdentityChange is not null);
    }

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
        HangAction = SelectedEnum(_hangAction, HangAction.LogOnly),
        StartupGraceSeconds = (int)_startupGrace.Value,
        RestartLoopProtectionEnabled = _loopProtection.Checked,
        MaxRestarts = (int)_maxRestarts.Value,
        RestartWindowMinutes = (int)_restartWindow.Value,
        BackoffMinutes = (int)_backoff.Value,
        HealthyResetMinutes = (int)_healthyReset.Value,
        GracefulShutdownSeconds = (int)_gracefulShutdown.Value,
        ForceKillAfterTimeout = _forceKill.Checked,
        // 子プロセスの扱いは現在「管理しない」だけなので、画面には出さず既存値を保つ。
        ChildProcessPolicy = _childPolicy,
        LogLevel = SelectedEnum(_logLevel, AppLogLevel.Information)
    };

    // ---- レイアウト部品 ----

    // 各行に枠を並べたタブ。行ごとに1つ以上の枠を横に並べ、最後の余白行で上に詰める。
    private static TabPage Page(string title, params GroupBox[][] rows)
    {
        var page = new TabPage(title) { Padding = new Padding(6), AutoScroll = true };
        var columns = rows.Max(r => r.Length);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            Padding = new Padding(8, 6, 8, 6)
        };
        for (var i = 0; i < columns; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        foreach (var groups in rows)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (var i = 0; i < groups.Length; i++)
            {
                layout.Controls.Add(groups[i], i, row);
                if (groups.Length == 1) layout.SetColumnSpan(groups[i], columns);
            }
        }
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);
        return page;
    }

    private static GroupBox Group(string title, out TableLayoutPanel table)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(4, 4, 4, 8)
        };
        table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(table);

        // 折り返す説明文は、枠の幅に合わせて最大幅を更新する。
        var inner = table;
        group.Resize += (_, _) =>
        {
            var width = Math.Max(100, group.ClientSize.Width - group.Padding.Horizontal - 8);
            foreach (var label in inner.Controls.OfType<Label>().Where(l => l.Tag as string == WrapTag))
                label.MaximumSize = new Size(width, 0);
        };
        return group;
    }

    private const string WrapTag = "wrap";

    private static Label WrappingLabel(Color? color = null, string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Color.DimGray,
        MaximumSize = new Size(380, 0),
        Tag = WrapTag
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control, bool fill = false)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 16, 7)
        };
        control.Margin = new Padding(3, 4, 22, 4);
        if (fill) control.Dock = DockStyle.Fill;
        else control.Anchor = AnchorStyles.Left;
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
    }

    // 狭い枠で選択肢の文字が切れないよう、ラベルの下に全幅で配置する。
    private static void AddStacked(TableLayoutPanel table, string label, Control control)
    {
        AddSpanning(table, new Label { Text = label, AutoSize = true });
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(3, 0, 22, 6);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static void AddSpanning(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = control is Label ? new Padding(3, 4, 3, 6) : new Padding(3, 6, 3, 6);
        control.Anchor = AnchorStyles.Left;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static TableLayoutPanel PathPicker(TextBox textBox, Button browse)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 4, 24, 4);
        browse.Dock = DockStyle.Fill;
        browse.Margin = new Padding(0, 2, 0, 2);
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private static Button BrowseButton(Action? click = null)
    {
        var button = new Button { Text = "...", Height = 30 };
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 110
    };

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Min(control.Maximum, Math.Max(control.Minimum, value));

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
