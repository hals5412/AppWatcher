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
        Width = 620;
        Height = 380;
        MinimumSize = new Size(560, 340);

        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.Auto, Localization.T("LanguageAuto")));
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.Japanese, Localization.T("LanguageJapanese")));
        _language.Items.Add(new LocalizedOption<UiLanguage>(UiLanguage.English, Localization.T("LanguageEnglish")));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildLogsTab());
        Controls.Add(tabs);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6)
        };
        var save = new Button { Text = Localization.T("ButtonSave"), AutoSize = true };
        save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Text = Localization.T("ButtonCancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
        Shown += async (_, _) => await LoadAsync();
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage(Localization.T("SettingsGeneralTab"));
        var table = CreateTable();
        AddRow(table, Localization.T("LanguageLabel"), _language);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildLogsTab()
    {
        var page = new TabPage(Localization.T("SettingsLogsTab"));
        var table = CreateTable();
        AddRow(table, Localization.T("EventRetentionDays"), _retentionDays);
        page.Controls.Add(table);
        return page;
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
            MessageBox.Show(this, Localization.T("SettingsSavedRestart"), "AppWatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private static TableLayoutPanel CreateTable() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        ColumnCount = 2,
        Padding = new Padding(12),
        ColumnStyles =
        {
            new ColumnStyle(SizeType.Absolute, 230),
            new ColumnStyle(SizeType.Percent, 100)
        }
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 8)
        }, 0, row);
        control.Margin = new Padding(3, 5, 3, 5);
        if (control is ComboBox)
        {
            control.Dock = DockStyle.Fill;
        }
        else
        {
            control.Anchor = AnchorStyles.Left;
        }
        table.Controls.Add(control, 1, row);
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 120
    };

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Min(control.Maximum, Math.Max(control.Minimum, value));
}
