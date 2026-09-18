using System.Text.Json;
using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class EventLogForm : Form
{
    private readonly EventStore _store = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _details = new();
    private readonly TextBox _appFilter = new();
    private readonly ComboBox _level = new();
    private readonly ComboBox _period = new();
    private IReadOnlyList<EventRecord> _records = [];

    public EventLogForm()
    {
        Text = "AppWatcher Event Log";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1050;
        Height = 650;
        MinimumSize = new Size(850, 500);

        var filter = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6),
            WrapContents = false
        };
        filter.Controls.Add(new Label { Text = "Application:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        _appFilter.Width = 180;
        filter.Controls.Add(_appFilter);
        filter.Controls.Add(new Label { Text = "Level:", AutoSize = true, Margin = new Padding(12, 7, 3, 0) });
        _level.DropDownStyle = ComboBoxStyle.DropDownList;
        _level.Items.Add("All");
        _level.Items.AddRange(Enum.GetNames<AppLogLevel>());
        _level.SelectedIndex = 0;
        filter.Controls.Add(_level);
        filter.Controls.Add(new Label { Text = "Period:", AutoSize = true, Margin = new Padding(12, 7, 3, 0) });
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.Items.AddRange(new object[] { "24 hours", "7 days", "30 days", "All" });
        _period.SelectedIndex = 0;
        filter.Controls.Add(_period);
        var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(12, 2, 3, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();
        filter.Controls.Add(refresh);
        Controls.Add(filter);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 400
        };
        ConfigureGrid();
        split.Panel1.Controls.Add(_grid);
        _details.Dock = DockStyle.Fill;
        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Both;
        _details.Font = new Font(FontFamily.GenericMonospace, 9);
        split.Panel2.Controls.Add(_details);
        Controls.Add(split);

        _appFilter.TextChanged += (_, _) => ApplyClientFilter();
        Shown += async (_, _) => await RefreshAsync();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time", Width = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "App", HeaderText = "Application", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Level", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = "Event", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "Reason", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.SelectionChanged += (_, _) => ShowSelectedDetails();
    }

    private async Task RefreshAsync()
    {
        AppLogLevel? minimum = null;
        if (_level.SelectedIndex > 0 && Enum.TryParse<AppLogLevel>(_level.SelectedItem?.ToString(), out var parsed))
        {
            minimum = parsed;
        }

        DateTimeOffset? since = _period.SelectedItem?.ToString() switch
        {
            "24 hours" => DateTimeOffset.UtcNow.AddHours(-24),
            "7 days" => DateTimeOffset.UtcNow.AddDays(-7),
            "30 days" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => null
        };

        try
        {
            _records = await _store.QueryAsync(new EventQuery(MinimumLevel: minimum, SinceUtc: since, Limit: 2000));
            ApplyClientFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not read event log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyClientFilter()
    {
        var text = _appFilter.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(text)
            ? _records
            : _records.Where(r => (r.ApplicationName ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        _grid.Rows.Clear();
        foreach (var record in filtered)
        {
            var index = _grid.Rows.Add(
                record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                record.ApplicationName ?? "(host)",
                record.Level,
                record.EventType,
                record.ReasonCode);
            _grid.Rows[index].Tag = record;
        }
        ShowSelectedDetails();
    }

    private void ShowSelectedDetails()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not EventRecord record)
        {
            _details.Clear();
            return;
        }

        string formattedDetails;
        try
        {
            using var doc = JsonDocument.Parse(record.DetailsJson);
            formattedDetails = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            formattedDetails = record.DetailsJson;
        }

        _details.Text = $"Timestamp : {record.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}\r\n" +
                        $"Application: {record.ApplicationName ?? "(host)"}\r\n" +
                        $"Level     : {record.Level}\r\n" +
                        $"Event     : {record.EventType}\r\n" +
                        $"Reason    : {record.ReasonCode}\r\n\r\n" +
                        formattedDetails;
    }
}
