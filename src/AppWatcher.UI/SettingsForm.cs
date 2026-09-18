using AppWatcher.Core;

namespace AppWatcher.UI;

internal sealed class SettingsForm : Form
{
    private readonly ConfigService _configService = new();
    private readonly ComboBox _language = new();
    private readonly NumericUpDown _retentionDays = Number(1, 3650);
    private AppWatcherConfiguration? _configuration;

    public SettingsForm()
    {
        Text = Localization.T("SettingsTitle");
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 640;
        Height = 250;
        MinimumSize = new Size(560, 235);

        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.Auto, Localization.T("LanguageAuto")));
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.Japanese, Localization.T("LanguageJapanese")));
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.English, Localization.T("LanguageEnglish")));

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(18, 18, 18, 10)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddSettingRow(content, 0, Localization.T("LanguageLabel"), _language);
        AddSettingRow(content, 1, Localization.T("EventRetentionDays"), _retentionDays);

        Controls.Add(content);

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
        save.Click += async (_, _) => await SaveAsync();

        var cancel = new Button
        {
            Text = Localization.T("ButtonCancel"),
            AutoSize = true,
            MinimumSize = new Size(80, 32),
            DialogResult = DialogResult.Cancel
        };

        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
        Shown += async (_, _) => await LoadAsync();
    }

    private static void AddSettingRow(
        TableLayoutPanel table,
        int row,
        string label,
        Control control)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 12, 8)
        };

        control.Margin = new Padding(3, 5, 3, 5);

        if (control is ComboBox)
        {
            control.Dock = DockStyle.Fill;
        }
        else
        {
            control.Anchor = AnchorStyles.Left;
        }

        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private async Task LoadAsync()
    {
        _configuration = await _configService.LoadAsync();
        SelectLanguage(_configuration.Global.Language);
        _retentionDays.Value = Clamp(_retentionDays, _configuration.Global.EventRetentionDays);
    }

    private async Task SaveAsync()
    {
        _configuration ??= await _configService.LoadAsync();
        var language = (_language.SelectedItem as LocalizedOption<UiLanguage>)?.Value ?? UiLanguage.Auto;
        var changedLanguage = _configuration.Global.Language != language;

        _configuration.Global.Language = language;
        _configuration.Global.EventRetentionDays = (int)_retentionDays.Value;
        await _configService.SaveAsync(_configuration);

        if (changedLanguage)
        {
            MessageBox.Show(
                this,
                Localization.T("SettingsSavedRestart"),
                "AppWatcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void SelectLanguage(UiLanguage language)
    {
        foreach (var item in _language.Items.OfType<LocalizedOption<UiLanguage>>())
        {
            if (item.Value == language)
            {
                _language.SelectedItem = item;
                return;
            }
        }

        _language.SelectedIndex = 0;
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 140
    };

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Min(control.Maximum, Math.Max(control.Minimum, value));
}
