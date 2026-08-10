using Snipping.Core.Ocr;

namespace Snipping.App;

/// <summary>
/// Floating OCR result editor. The overlay owns positioning and image-box
/// synchronization; this control owns text selection and copying.
/// </summary>
public sealed class OcrResultPanel : RoundedPanel
{
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly TextBox _resultTextBox;
    private readonly Button _copyButton;
    private readonly string _language;
    private IReadOnlyList<OcrTextLine> _lines = Array.Empty<OcrTextLine>();
    private int[] _lineStarts = Array.Empty<int>();
    private bool _suppressSelectionEvent;

    public event EventHandler<int>? LineSelected;

    public OcrResultPanel(string? language = null)
    {
        _language = language ?? "zh-CN";
        Size = new Size(360, 260);
        Padding = new Padding(8);
        BackColor = Color.FromArgb(32, 32, 32);
        BorderColor = Color.FromArgb(96, 96, 96);
        CornerRadius = 8;
        TintColor = Color.FromArgb(235, 32, 32, 32);
        TabStop = true;

        _titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = UiText.OcrResultTitle(_language),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "",
            ForeColor = Color.FromArgb(190, 190, 190),
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _copyButton = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Text = UiText.CopyText(_language),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 },
            TabStop = true
        };
        _copyButton.Click += (_, _) => CopySelected();

        _resultTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(42, 42, 42),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            ShortcutsEnabled = true,
            TabStop = true,
            AccessibleName = UiText.OcrResultTitle(_language)
        };
        _resultTextBox.MouseUp += (_, _) => NotifyTextSelection();
        _resultTextBox.KeyUp += (_, _) => NotifyTextSelection();
        _resultTextBox.GotFocus += (_, _) => NotifyTextSelection();

        Controls.Add(_resultTextBox);
        Controls.Add(_statusLabel);
        Controls.Add(_copyButton);
        Controls.Add(_titleLabel);
    }

    public bool IsTextEditorFocused => _resultTextBox.Focused;

    public void SetLoading()
    {
        _lines = Array.Empty<OcrTextLine>();
        _lineStarts = Array.Empty<int>();
        _resultTextBox.Clear();
        _copyButton.Enabled = false;
        _statusLabel.ForeColor = Color.FromArgb(190, 190, 190);
        _statusLabel.Text = UiText.Recognizing(_language);
    }

    public void SetResult(
        IReadOnlyList<OcrTextLine> lines,
        string? errorMessage = null,
        string? infoMessage = null)
    {
        _lines = lines;
        _lineStarts = new int[lines.Count];
        var offset = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            _lineStarts[i] = offset;
            offset += lines[i].Text.Length + Environment.NewLine.Length;
        }

        _suppressSelectionEvent = true;
        try
        {
            _resultTextBox.Text = string.Join(Environment.NewLine, lines.Select(static line => line.Text));
            _resultTextBox.SelectionStart = 0;
            _resultTextBox.SelectionLength = 0;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }

        _copyButton.Enabled = lines.Count > 0;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _statusLabel.ForeColor = Color.FromArgb(255, 190, 120);
            _statusLabel.Text = errorMessage;
        }
        else if (lines.Count == 0)
        {
            _statusLabel.ForeColor = Color.FromArgb(190, 190, 190);
            _statusLabel.Text = UiText.NoTextRecognized(_language);
        }
        else if (!string.IsNullOrWhiteSpace(infoMessage))
        {
            _statusLabel.ForeColor = Color.FromArgb(170, 210, 235);
            _statusLabel.Text = infoMessage;
            SelectLine(0);
        }
        else
        {
            _statusLabel.ForeColor = Color.FromArgb(150, 210, 170);
            _statusLabel.Text = UiText.RecognizedLines(_language, lines.Count);
            SelectLine(0);
        }

        UpdatePanelHeight();
    }

    public void SelectLine(int index)
    {
        if (index < 0 || index >= _lines.Count)
            return;

        if (index >= _lineStarts.Length)
            return;
        var start = _lineStarts[index];

        _suppressSelectionEvent = true;
        try
        {
            _resultTextBox.Select(start, _lines[index].Text.Length);
            _resultTextBox.ScrollToCaret();
        }
        finally
        {
            _suppressSelectionEvent = false;
        }

        Invalidate();
    }

    public void CopySelected()
    {
        var text = _resultTextBox.SelectedText;
        if (string.IsNullOrEmpty(text) && _lines.Count > 0)
        {
            var index = GetLineIndexAtSelection();
            if (index >= 0 && index < _lines.Count)
                text = _lines[index].Text;
        }

        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _statusLabel.ForeColor = Color.FromArgb(150, 210, 170);
            _statusLabel.Text = UiText.TextCopied(_language);
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(255, 150, 150);
            _statusLabel.Text = UiText.CopyFailed(_language, ex.Message);
        }
    }

    private void NotifyTextSelection()
    {
        if (_suppressSelectionEvent || _lines.Count == 0)
            return;

        var index = GetLineIndexAtSelection();
        if (index >= 0)
            LineSelected?.Invoke(this, index);
    }

    private int GetLineIndexAtSelection()
    {
        if (_resultTextBox.TextLength == 0)
            return -1;

        var position = Math.Clamp(_resultTextBox.SelectionStart, 0, _resultTextBox.TextLength);
        var index = 0;
        for (var i = 1; i < _lineStarts.Length; i++)
        {
            if (_lineStarts[i] > position)
                break;
            index = i;
        }

        return index;
    }

    private void UpdatePanelHeight()
    {
        var desired = 24 + 30 + 24 + Math.Clamp(_lines.Count * 22, 66, 220) + Padding.Vertical;
        Height = Math.Clamp(desired, 170, 320);
    }
}
