using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class RunningApplicationsForm : Form
{
    private readonly IReadOnlyList<RunningProcessInfo> _processes;
    private readonly HashSet<string> _registeredPaths;
    private readonly DataGridView _grid = new();
    private readonly TextBox _filter = new();
    private readonly Button _addSelected = new();

    public RunningProcessInfo? SelectedProcess { get; private set; }

    public RunningApplicationsForm(
        IReadOnlyList<RunningProcessInfo> processes,
        IEnumerable<string> registeredExecutablePaths)
    {
        _processes = processes;
        _registeredPaths = registeredExecutablePaths
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Text = Localization.T("RunningAppsTitle");
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 1120;
        Height = 650;
        MinimumSize = new Size(860, 480);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        // 列幅をウィンドウに合わせる。未指定だと説明文の1行分の幅まで列が広がり、
        // 絞り込み欄・一覧・右下のボタンがウィンドウ外へはみ出す。
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var info = new Label
        {
            Text = Localization.T("RunningAppsInfo"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 3, 3, 10)
        };
        layout.Controls.Add(info, 0, 0);

        var filterPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        filterPanel.Controls.Add(new Label
        {
            Text = Localization.T("RunningAppsFilterLabel"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 8, 3)
        }, 0, 0);

        _filter.Dock = DockStyle.Fill;
        _filter.PlaceholderText = Localization.T("RunningAppsFilterPlaceholder");
        _filter.TextChanged += (_, _) => PopulateRows();
        filterPanel.Controls.Add(_filter, 1, 0);
        layout.Controls.Add(filterPanel, 0, 1);

        ConfigureGrid();
        layout.Controls.Add(_grid, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
            WrapContents = false
        };

        _addSelected.Text = Localization.T("RunningAppsAddSelected");
        _addSelected.AutoSize = true;
        _addSelected.MinimumSize = new Size(150, 32);
        _addSelected.Enabled = false;
        _addSelected.Click += (_, _) => AcceptSelected();

        var cancel = new Button
        {
            Text = Localization.T("ButtonCancel"),
            AutoSize = true,
            MinimumSize = new Size(80, 32),
            DialogResult = DialogResult.Cancel
        };

        buttons.Controls.Add(_addSelected);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
        AcceptButton = _addSelected;
        CancelButton = cancel;

        PopulateRows();
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
        _grid.ColumnHeadersHeight = 32;
        _grid.RowTemplate.Height = 30;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.ShowCellToolTips = true;

        _grid.Columns.Add(Column("Application", Localization.T("ColumnApplication"), 180));
        _grid.Columns.Add(Column("PID", "PID", 70));
        _grid.Columns.Add(Column("Instances", Localization.T("RunningAppsColumnInstances"), 80));
        _grid.Columns.Add(Column("Privilege", Localization.T("ColumnPrivilege"), 100));
        _grid.Columns.Add(Column("WindowTitle", Localization.T("RunningAppsColumnWindowTitle"), 240));
        _grid.Columns.Add(Column("Executable", Localization.T("ColumnExecutable"), 440));
        _grid.Columns.Add(Column("Registration", Localization.T("RunningAppsColumnRegistration"), 120));

        _grid.SelectionChanged += (_, _) => UpdateSelectionState();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                AcceptSelected();
            }
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            e.ToolTipText = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].FormattedValue?.ToString();
        };
    }

    private static DataGridViewTextBoxColumn Column(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        MinimumWidth = 50,
        SortMode = DataGridViewColumnSortMode.Automatic
    };

    private void PopulateRows()
    {
        var filter = _filter.Text.Trim();
        var selectedPath = SelectedRowProcess()?.ExecutablePath;

        _grid.Rows.Clear();

        foreach (var process in _processes)
        {
            var privilegeText = Localization.PrivilegeText(process.Privilege);
            if (filter.Length > 0 &&
                !Contains(process.Name, filter) &&
                !Contains(process.WindowTitle, filter) &&
                !Contains(process.ExecutablePath, filter) &&
                !Contains(privilegeText, filter))
            {
                continue;
            }

            var registered = IsRegistered(process);
            var index = _grid.Rows.Add(
                process.Name,
                process.ProcessId,
                process.InstanceCount,
                privilegeText,
                process.WindowTitle,
                process.ExecutablePath,
                registered
                    ? Localization.T("RunningAppsAlreadyRegistered")
                    : Localization.T("RunningAppsAvailable"));

            var row = _grid.Rows[index];
            row.Tag = process;

            if (registered)
            {
                row.DefaultCellStyle.ForeColor = Color.DimGray;
            }

            if (string.Equals(
                    selectedPath,
                    process.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = true;
            }
        }

        if (_grid.Rows.Count > 0 && _grid.SelectedRows.Count == 0)
        {
            _grid.Rows[0].Selected = true;
            _grid.CurrentCell = _grid.Rows[0].Cells[0];
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var selected = SelectedRowProcess();
        _addSelected.Enabled = selected is not null && !IsRegistered(selected);
    }

    private void AcceptSelected()
    {
        var selected = SelectedRowProcess();
        if (selected is null || IsRegistered(selected))
        {
            return;
        }

        SelectedProcess = selected;
        DialogResult = DialogResult.OK;
        Close();
    }

    private RunningProcessInfo? SelectedRowProcess() =>
        _grid.SelectedRows.Count == 0
            ? null
            : _grid.SelectedRows[0].Tag as RunningProcessInfo;

    private bool IsRegistered(RunningProcessInfo process) =>
        _registeredPaths.Contains(NormalizePath(process.ExecutablePath));

    private static bool Contains(string? value, string filter) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

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
}
