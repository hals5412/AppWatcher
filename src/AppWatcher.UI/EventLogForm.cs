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
    private readonly Guid? _applicationId;
    private readonly string? _applicationName;
    private IReadOnlyList<EventRecord> _records = [];

    public EventLogForm(
        Guid? applicationId = null,
        string? applicationName = null)
    {
        _applicationId = applicationId;
        _applicationName = applicationName;

        Text = _applicationId is null
            ? Localization.T("EventLogTitle")
            : Localization.F(
                "EventLogTitleForApplication",
                _applicationName ?? _applicationId.Value.ToString("D"));
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
        filter.Controls.Add(new Label { Text = Localization.T("LabelApplication"), AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        _appFilter.Width = 220;
        if (_applicationId is not null)
        {
            _appFilter.Text = _applicationName ?? _applicationId.Value.ToString("D");
            _appFilter.ReadOnly = true;
            _appFilter.BackColor = SystemColors.Control;
            _appFilter.TabStop = false;
        }
        filter.Controls.Add(_appFilter);
        filter.Controls.Add(new Label { Text = Localization.T("LabelLevel"), AutoSize = true, Margin = new Padding(12, 7, 3, 0) });
        _level.DropDownStyle = ComboBoxStyle.DropDownList;
        _level.Items.Add(new LocalizedOption<AppLogLevel?>(null, Localization.T("All")));
        foreach (var value in Enum.GetValues<AppLogLevel>())
        {
            _level.Items.Add(new LocalizedOption<AppLogLevel?>(value, Localization.LogLevelText(value)));
        }
        _level.SelectedIndex = 0;
        filter.Controls.Add(_level);
        filter.Controls.Add(new Label { Text = Localization.T("LabelPeriod"), AutoSize = true, Margin = new Padding(12, 7, 3, 0) });
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.Items.Add(new LocalizedOption<TimeSpan?>(TimeSpan.FromHours(24), Localization.T("Period24Hours")));
        _period.Items.Add(new LocalizedOption<TimeSpan?>(TimeSpan.FromDays(7), Localization.T("Period7Days")));
        _period.Items.Add(new LocalizedOption<TimeSpan?>(TimeSpan.FromDays(30), Localization.T("Period30Days")));
        _period.Items.Add(new LocalizedOption<TimeSpan?>(null, Localization.T("All")));
        _period.SelectedIndex = 0;
        filter.Controls.Add(_period);
        var refresh = new Button { Text = Localization.T("ButtonRefresh"), AutoSize = true, Margin = new Padding(12, 2, 3, 0) };
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = Localization.T("ColumnTime"), Width = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "App", HeaderText = Localization.T("ColumnApplication"), Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = Localization.T("ColumnLevel"), Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = Localization.T("ColumnEvent"), Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = Localization.T("ColumnReason"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.SelectionChanged += (_, _) => ShowSelectedDetails();
    }

    private async Task RefreshAsync()
    {
        var minimum = (_level.SelectedItem as LocalizedOption<AppLogLevel?>)?.Value;
        var period = (_period.SelectedItem as LocalizedOption<TimeSpan?>)?.Value;
        var since = period is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow - period.Value;

        try
        {
            _records = await _store.QueryAsync(new EventQuery(
                ApplicationId: _applicationId,
                MinimumLevel: minimum,
                SinceUtc: since,
                Limit: 2000));
            ApplyClientFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Localization.T("CouldNotReadEventLog"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyClientFilter()
    {
        // Scoped log windows query by stable application ID. Do not apply the
        // current display name as a second filter: renamed applications should
        // still show events recorded under their previous names.
        var text = _applicationId is null
            ? _appFilter.Text.Trim()
            : string.Empty;

        var filtered = string.IsNullOrWhiteSpace(text)
            ? _records
            : _records.Where(r =>
                (r.ApplicationName ?? string.Empty).Contains(
                    text,
                    StringComparison.CurrentCultureIgnoreCase)).ToArray();

        _grid.Rows.Clear();
        foreach (var record in filtered)
        {
            var index = _grid.Rows.Add(
                record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                record.ApplicationName ?? Localization.T("HostLabel"),
                Localization.LogLevelText(record.Level),
                Localization.EventCode(record.EventType),
                Localization.ReasonCode(record.ReasonCode));
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

        _details.Text =
            $"{Localization.T("DetailTimestamp"),-12}: {record.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}\r\n" +
            $"{Localization.T("DetailApplication"),-12}: {record.ApplicationName ?? Localization.T("HostLabel")}\r\n" +
            $"{Localization.T("DetailLevel"),-12}: {Localization.LogLevelText(record.Level)}\r\n" +
            $"{Localization.T("DetailEvent"),-12}: {Localization.EventCode(record.EventType)}\r\n" +
            $"{Localization.T("DetailReason"),-12}: {Localization.ReasonCode(record.ReasonCode)}\r\n" +
            $"{Localization.T("DetailEventCode"),-12}: {record.EventType}\r\n" +
            $"{Localization.T("DetailReasonCode"),-12}: {record.ReasonCode}\r\n\r\n" +
            formattedDetails;
    }
}
