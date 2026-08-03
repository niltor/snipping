using System.Runtime.InteropServices;
using Microsoft.Win32;
using Snipping.Core.Settings;

namespace Snipping.App;

public sealed class HotkeySettingsForm : Form
{
    private readonly TextBox _hotkeyTextBox;
    private readonly TextBox _saveDirectoryTextBox;
    private readonly TextBox _pinShortcutTextBox;
    private readonly NumericUpDown _pinOpacityNumeric;
    private readonly Panel _card;
    private readonly Label _hotkeyLabel;
    private readonly Label _pinLabel;
    private readonly Label _pinOpacityLabel;
    private readonly Label _saveLabel;
    private readonly Label _hotkeyHint;
    private readonly Label _themeLabel;
    private readonly Label _languageLabel;
    private readonly GroupBox _themeGroup;
    private readonly RadioButton _themeSystemRadio;
    private readonly RadioButton _themeLightRadio;
    private readonly RadioButton _themeDarkRadio;
    private readonly ComboBox _languageCombo;
    private readonly Button _okButton;
    private readonly Button _cancelButton;
    private readonly Button _browseButton;
    private readonly Icon _windowIcon;

    private Color _cardBorderColor;
    private bool _currentDark;

    public string Hotkey => _hotkeyTextBox.Text.Trim();
    public string SaveDirectory => _saveDirectoryTextBox.Text.Trim();
    public string PinShortcut => _pinShortcutTextBox.Text.Trim();
    public int PinOpacity => (int)_pinOpacityNumeric.Value;
    public string Theme => _themeDarkRadio.Checked ? "Dark" : _themeLightRadio.Checked ? "Light" : "System";
    public string Language => (_languageCombo.SelectedItem as LanguageOption)?.Value ?? "zh-CN";

    public HotkeySettingsForm(SnippingSettings settings)
    {
        Text = "截图设置";
        _windowIcon = AppIcon.Create();
        Icon = _windowIcon;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 460);
        Size = new Size(720, 510);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _card = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 8)
        };
        _card.Paint += (_, e) =>
        {
            using var pen = new Pen(_cardBorderColor, 1);
            var r = _card.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // hotkey
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // pin
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // pin opacity
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // save
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // theme
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // language
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _hotkeyLabel = CreateLabel("全局截图快捷键");
        _hotkeyTextBox = CreateInputBox(settings.Hotkey, readOnly: true);
        AttachShortcutCapture(_hotkeyTextBox);

        _pinLabel = CreateLabel("置顶贴图快捷键");
        _pinShortcutTextBox = CreateInputBox(settings.PinShortcut, readOnly: true);
        AttachShortcutCapture(_pinShortcutTextBox);

        _pinOpacityLabel = CreateLabel("贴图不透明度 (%)");
        _pinOpacityNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 100,
            Value = Math.Clamp(settings.PinOpacity, 1, 100),
            Increment = 1,
            Width = 120,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 6)
        };

        _saveLabel = CreateLabel("保存目录");
        _saveDirectoryTextBox = CreateInputBox(settings.SaveDirectory, readOnly: false);
        _saveDirectoryTextBox.Margin = new Padding(0, 0, 8, 0);

        _browseButton = new Button
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            Text = "浏览"
        };
        StyleButton(_browseButton);
        _browseButton.Click += (_, _) => BrowseFolder();

        _themeLabel = CreateLabel("主题");
        _themeGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 2, 0, 6)
        };
        var themeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };
        _themeSystemRadio = new RadioButton { AutoSize = true, Margin = new Padding(0, 0, 20, 0) };
        _themeLightRadio = new RadioButton { AutoSize = true, Margin = new Padding(0, 0, 20, 0) };
        _themeDarkRadio = new RadioButton { AutoSize = true, Margin = new Padding(0) };
        _themeSystemRadio.CheckedChanged += (_, _) => ApplyTheme();
        _themeLightRadio.CheckedChanged += (_, _) => ApplyTheme();
        _themeDarkRadio.CheckedChanged += (_, _) => ApplyTheme();
        themeFlow.Controls.Add(_themeSystemRadio);
        themeFlow.Controls.Add(_themeLightRadio);
        themeFlow.Controls.Add(_themeDarkRadio);
        _themeGroup.Controls.Add(themeFlow);

        _languageLabel = CreateLabel("语言");
        _languageCombo = new ComboBox
        {
            Dock = DockStyle.Left,
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 2, 0, 6)
        };
        _languageCombo.Items.Add(new LanguageOption("zh-CN", "中文 (简体)"));
        _languageCombo.Items.Add(new LanguageOption("en-US", "English"));
        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            ApplyLanguageTexts();
            ApplyTheme();
        };

        _hotkeyHint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "点击输入框后\n直接按组合键\n(如 Ctrl+Shift+S)",
            BackColor = Color.Transparent,
            Padding = new Padding(6, 1, 0, 0)
        };

        var saveRowPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
            Height = 32
        };
        saveRowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        saveRowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        saveRowPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        saveRowPanel.Controls.Add(_saveDirectoryTextBox, 0, 0);
        saveRowPanel.Controls.Add(_browseButton, 1, 0);

        grid.Controls.Add(_hotkeyLabel, 0, 0);
        grid.Controls.Add(_hotkeyTextBox, 1, 0);
        grid.Controls.Add(_hotkeyHint, 2, 0);
        grid.SetRowSpan(_hotkeyHint, 2);

        grid.Controls.Add(_pinLabel, 0, 1);
        grid.Controls.Add(_pinShortcutTextBox, 1, 1);

        grid.Controls.Add(_pinOpacityLabel, 0, 2);
        grid.Controls.Add(_pinOpacityNumeric, 1, 2);

        grid.Controls.Add(_saveLabel, 0, 3);
        grid.Controls.Add(saveRowPanel, 1, 3);
        grid.SetColumnSpan(saveRowPanel, 2);

        grid.Controls.Add(_themeLabel, 0, 4);
        grid.Controls.Add(_themeGroup, 1, 4);
        grid.SetColumnSpan(_themeGroup, 2);

        grid.Controls.Add(_languageLabel, 0, 5);
        grid.Controls.Add(_languageCombo, 1, 5);

        _card.Controls.Add(grid);

        _okButton = new Button
        {
            Text = "保存",
            AutoSize = true,
            DialogResult = DialogResult.OK
        };
        StyleButton(_okButton);

        _cancelButton = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        StyleButton(_cancelButton);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        footer.Controls.Add(_cancelButton);
        footer.Controls.Add(_okButton);

        root.Controls.Add(_card, 0, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);

        SetThemeSelection(settings.Theme);
        SetLanguageSelection(settings.Language);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        ApplyLanguageTexts();
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowBackdrop();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _windowIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Label CreateLabel(string text) => new()
    {
        Dock = DockStyle.None,
        Anchor = AnchorStyles.Left,
        AutoSize = true,
        Margin = new Padding(0, 4, 6, 4),
        Text = text,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static TextBox CreateInputBox(string value, bool readOnly) => new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 2, 0, 6),
        Text = value,
        ReadOnly = readOnly,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 90);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 80);
        button.MinimumSize = new Size(80, 32);
        button.Padding = new Padding(12, 2, 12, 2);
    }

    private void SetThemeSelection(string theme)
    {
        switch ((theme ?? "System").Trim().ToLowerInvariant())
        {
            case "dark":
                _themeDarkRadio.Checked = true;
                break;
            case "light":
                _themeLightRadio.Checked = true;
                break;
            default:
                _themeSystemRadio.Checked = true;
                break;
        }
    }

    private void SetLanguageSelection(string language)
    {
        var match = _languageCombo.Items
            .OfType<LanguageOption>()
            .FirstOrDefault(x => x.Value.Equals(language ?? "", StringComparison.OrdinalIgnoreCase));
        _languageCombo.SelectedItem = match ?? _languageCombo.Items.OfType<LanguageOption>().First(x => x.Value == "zh-CN");
    }

    private bool IsEnglishSelected() => Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    private void ApplyLanguageTexts()
    {
        var en = IsEnglishSelected();

        Text = en ? "Snipping Settings" : "截图设置";
        _hotkeyLabel.Text = en ? "Global snip shortcut" : "全局截图快捷键";
        _pinLabel.Text = en ? "Pin shortcut" : "置顶贴图快捷键";
        _saveLabel.Text = en ? "Save directory" : "保存目录";
        _themeLabel.Text = en ? "Theme" : "主题";
        _languageLabel.Text = en ? "Language" : "语言";

        _hotkeyHint.Text = en
            ? "Click a shortcut box,\nthen press keys directly\n(e.g. Ctrl+Shift+S)"
            : "点击输入框后\n直接按组合键\n(如 Ctrl+Shift+S)";

        _themeGroup.Text = en ? "Appearance" : "外观";
        _themeSystemRadio.Text = en ? "System" : "跟随系统";
        _themeLightRadio.Text = en ? "Light" : "浅色";
        _themeDarkRadio.Text = en ? "Dark" : "深色";

        _browseButton.Text = en ? "Browse" : "浏览";
        _okButton.Text = en ? "Save" : "保存";
        _cancelButton.Text = en ? "Cancel" : "取消";
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            if (IsHandleCreated && _themeSystemRadio.Checked)
                BeginInvoke(new Action(() =>
                {
                    ApplyTheme();
                    ApplyWindowBackdrop();
                }));
        }
    }

    private void ApplyTheme()
    {
        var dark = Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDarkThemePreferred()
        };
        _currentDark = dark;

        var bg = dark ? Color.FromArgb(22, 22, 22) : Color.FromArgb(243, 243, 243);
        var cardBg = dark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(250, 250, 250);
        _cardBorderColor = dark ? Color.FromArgb(78, 78, 78) : Color.FromArgb(198, 198, 198);
        var text = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(20, 20, 20);
        var subText = dark ? Color.FromArgb(184, 184, 184) : Color.FromArgb(90, 90, 90);
        var inputBg = dark ? Color.FromArgb(46, 46, 46) : Color.White;

        BackColor = bg;
        ForeColor = text;

        _card.BackColor = cardBg;

        _hotkeyLabel.ForeColor = text;
        _pinLabel.ForeColor = text;
        _pinOpacityLabel.ForeColor = text;
        _saveLabel.ForeColor = text;
        _themeLabel.ForeColor = text;
        _languageLabel.ForeColor = text;
        _themeGroup.ForeColor = text;
        _themeGroup.BackColor = Color.Transparent;
        _themeSystemRadio.ForeColor = text;
        _themeLightRadio.ForeColor = text;
        _themeDarkRadio.ForeColor = text;
        _themeSystemRadio.BackColor = Color.Transparent;
        _themeLightRadio.BackColor = Color.Transparent;
        _themeDarkRadio.BackColor = Color.Transparent;
        _hotkeyHint.ForeColor = subText;

        ApplyTextBoxTheme(_hotkeyTextBox, inputBg, text);
        ApplyTextBoxTheme(_pinShortcutTextBox, inputBg, text);
        ApplyTextBoxTheme(_saveDirectoryTextBox, inputBg, text);
        _pinOpacityNumeric.BackColor = inputBg;
        _pinOpacityNumeric.ForeColor = text;

        _languageCombo.BackColor = inputBg;
        _languageCombo.ForeColor = text;

        ApplyButtonTheme(_okButton, primary: true, dark);
        ApplyButtonTheme(_cancelButton, primary: false, dark);
        ApplyButtonTheme(_browseButton, primary: false, dark);

        _card.Invalidate();
        ApplyWindowBackdrop();
    }

    private static void ApplyButtonTheme(Button button, bool primary, bool dark)
    {
        if (primary)
        {
            button.BackColor = Color.FromArgb(0, 120, 212);
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 212);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 108, 193);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 96, 172);
        }
        else
        {
            if (dark)
            {
                button.BackColor = Color.FromArgb(58, 58, 58);
                button.ForeColor = Color.FromArgb(245, 245, 245);
                button.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 96);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(74, 74, 74);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(86, 86, 86);
            }
            else
            {
                button.BackColor = Color.FromArgb(245, 245, 245);
                button.ForeColor = Color.FromArgb(28, 28, 28);
                button.FlatAppearance.BorderColor = Color.FromArgb(204, 204, 204);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 235, 235);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 226, 226);
            }
        }
    }

    private static void ApplyTextBoxTheme(TextBox textBox, Color backColor, Color foreColor)
    {
        textBox.BackColor = backColor;
        textBox.ForeColor = foreColor;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    private static bool IsSystemDarkThemePreferred()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
                return intValue == 0;
        }
        catch
        {
            // fallback below
        }

        return false;
    }

    private void ApplyWindowBackdrop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        try
        {
            var dark = Theme switch
            {
                "Dark" => 1,
                "Light" => 0,
                _ => IsSystemDarkThemePreferred() ? 1 : 0
            };
            _ = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var captionColor = _currentDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(245, 245, 245);
                var textColor = _currentDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(20, 20, 20);
                var cap = ColorTranslator.ToWin32(captionColor);
                var txt = ColorTranslator.ToWin32(textColor);
                _ = DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));
                _ = DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
            }

            var backdrop = DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
            {
                backdrop = DWMSBT_TRANSIENTWINDOW;
                _ = DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // ignore unsupported platform attributes
        }
    }

    private void BrowseFolder()
    {
        var en = IsEnglishSelected();
        using var dialog = new FolderBrowserDialog
        {
            Description = en ? "Select the screenshot save directory" : "选择截图保存目录",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_saveDirectoryTextBox.Text)
                ? _saveDirectoryTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _saveDirectoryTextBox.Text = dialog.SelectedPath;
    }

    private static void AttachShortcutCapture(TextBox box)
    {
        box.ShortcutsEnabled = false;
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Tab)
                return;

            if (e.KeyCode is Keys.Back or Keys.Delete)
            {
                box.Clear();
                e.SuppressKeyPress = true;
                return;
            }

            var shortcut = BuildShortcutText(e);
            if (!string.IsNullOrEmpty(shortcut))
                box.Text = shortcut;

            e.SuppressKeyPress = true;
        };
    }

    private static string? BuildShortcutText(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
            return null;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Shift) parts.Add("Shift");
        if (e.Alt) parts.Add("Alt");
        parts.Add(NormalizeKeyName(e.KeyCode));
        return string.Join("+", parts);
    }

    private static string NormalizeKeyName(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
            return ((int)(key - Keys.D0)).ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return "Num" + ((int)(key - Keys.NumPad0));

        return key switch
        {
            Keys.OemMinus => "Minus",
            Keys.Oemplus => "Plus",
            Keys.Oemcomma => "Comma",
            Keys.OemPeriod => "Period",
            Keys.OemQuestion => "Slash",
            Keys.OemOpenBrackets => "LeftBracket",
            Keys.Oem6 => "RightBracket",
            Keys.Oem5 => "Backslash",
            Keys.Oem1 => "Semicolon",
            Keys.Oem7 => "Quote",
            Keys.Oemtilde => "Tilde",
            _ => key.ToString()
        };
    }

    private sealed record LanguageOption(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
