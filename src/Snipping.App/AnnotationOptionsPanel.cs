namespace Snipping.App;

/// <summary>
/// Compact second-row toolbar for the options of the currently selected tool.
/// It uses the same icon-button language as the primary toolbar; descriptions
/// are provided through tooltips and accessibility names.
/// </summary>
internal sealed class AnnotationOptionsPanel : RoundedPanel
{
    private readonly string _language;
    private readonly FlowLayoutPanel _content;
    private readonly ToolTip _toolTip = new()
    {
        InitialDelay = 300,
        ReshowDelay = 200,
        AutoPopDelay = 4000
    };
    private readonly List<(RoundedButton Button, Func<bool> IsSelected)> _toggleButtons = [];
    private AnnotationToolOptions? _options;

    public event EventHandler? OptionsChanged;

    public AnnotationOptionsPanel(string? language)
    {
        _language = language ?? "zh-CN";
        Height = 44;
        BackColor = Color.FromArgb(30, 30, 30);
        CornerRadius = 8;
        BorderColor = Color.FromArgb(80, 80, 80);
        BorderThickness = 1;
        TintColor = Color.FromArgb(255, 30, 30, 30);

        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(7, 4, 7, 4),
            BackColor = Color.Transparent
        };
        Controls.Add(_content);
    }

    public static bool Supports(AnnotationTool tool) => tool is
        AnnotationTool.Rectangle or
        AnnotationTool.Ellipse or
        AnnotationTool.Arrow or
        AnnotationTool.Line or
        AnnotationTool.Text or
        AnnotationTool.Mosaic;

    public void Bind(AnnotationTool tool, AnnotationToolOptions options)
    {
        _options = options;
        _options.Normalize();
        ClearContent();

        switch (tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                AddTrackBar(
                    UiText.T(_language, "透明度", "Opacity"),
                    10,
                    100,
                    _options.ShapeOpacity,
                    value => _options.ShapeOpacity = value);
                AddSeparator();
                AddToggle(
                    ToolIcons.ShapeOutline,
                    UiText.T(_language, "边框", "Outline"),
                    () => _options.ShapeMode == ShapeRenderMode.Outline,
                    () => _options.ShapeMode = ShapeRenderMode.Outline);
                AddToggle(
                    ToolIcons.ShapeFill,
                    UiText.T(_language, "填充", "Fill"),
                    () => _options.ShapeMode == ShapeRenderMode.Fill,
                    () => _options.ShapeMode = ShapeRenderMode.Fill);
                break;

            case AnnotationTool.Arrow:
                AddToggle(
                    ToolIcons.ArrowSingle,
                    UiText.T(_language, "单箭头", "Single arrow"),
                    () => _options.ArrowHead == ArrowHeadMode.Single
                        && _options.ArrowStrokeStyle == LineStrokeStyle.Solid,
                    () =>
                    {
                        _options.ArrowHead = ArrowHeadMode.Single;
                        _options.ArrowStrokeStyle = LineStrokeStyle.Solid;
                    });
                AddToggle(
                    ToolIcons.ArrowDouble,
                    UiText.T(_language, "双箭头", "Double arrow"),
                    () => _options.ArrowHead == ArrowHeadMode.Double
                        && _options.ArrowStrokeStyle == LineStrokeStyle.Solid,
                    () =>
                    {
                        _options.ArrowHead = ArrowHeadMode.Double;
                        _options.ArrowStrokeStyle = LineStrokeStyle.Solid;
                    });
                AddToggle(
                    ToolIcons.ArrowDashedSingle,
                    UiText.T(_language, "虚线单箭头", "Dashed single arrow"),
                    () => _options.ArrowHead == ArrowHeadMode.Single
                        && _options.ArrowStrokeStyle == LineStrokeStyle.Dashed,
                    () =>
                    {
                        _options.ArrowHead = ArrowHeadMode.Single;
                        _options.ArrowStrokeStyle = LineStrokeStyle.Dashed;
                    });
                break;

            case AnnotationTool.Line:
                AddToggle(
                    ToolIcons.LineSolid,
                    UiText.T(_language, "实线", "Solid line"),
                    () => _options.LineStyle == LineStrokeStyle.Solid,
                    () => _options.LineStyle = LineStrokeStyle.Solid);
                AddToggle(
                    ToolIcons.LineDashed,
                    UiText.T(_language, "虚线", "Dashed line"),
                    () => _options.LineStyle == LineStrokeStyle.Dashed,
                    () => _options.LineStyle = LineStrokeStyle.Dashed);
                AddToggle(
                    ToolIcons.LineDotted,
                    UiText.T(_language, "点线", "Dotted line"),
                    () => _options.LineStyle == LineStrokeStyle.Dotted,
                    () => _options.LineStyle = LineStrokeStyle.Dotted);
                break;

            case AnnotationTool.Text:
                AddTrackBar(
                    UiText.T(_language, "字号", "Font size"),
                    10,
                    100,
                    _options.TextFontSize,
                    value => _options.TextFontSize = value,
                    tickFrequency: 10,
                    width: 132);
                AddToggle(
                    ToolIcons.Bold,
                    UiText.T(_language, "粗体", "Bold"),
                    () => _options.TextBold,
                    () => _options.TextBold = !_options.TextBold);
                AddToggle(
                    ToolIcons.Italic,
                    UiText.T(_language, "斜体", "Italic"),
                    () => _options.TextItalic,
                    () => _options.TextItalic = !_options.TextItalic);
                break;

            case AnnotationTool.Mosaic:
                AddTrackBar(
                    UiText.T(_language, "范围", "Brush size"),
                    5,
                    50,
                    _options.MosaicBrushWidth,
                    value => _options.MosaicBrushWidth = value,
                    tickFrequency: 5,
                    width: 132);
                break;
        }

        RefreshToggles();
        FitToContent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void ClearContent()
    {
        _toggleButtons.Clear();
        foreach (Control control in _content.Controls)
            control.Dispose();
        _content.Controls.Clear();
    }

    private RoundedButton CreateIconButton(
        Action<Graphics, Rectangle, Color> icon,
        string tooltip,
        bool interactive)
    {
        var button = new RoundedButton
        {
            Size = new Size(30, 30),
            CornerRadius = 5,
            IdleColor = Color.FromArgb(38, 38, 38),
            HoverColor = Color.FromArgb(55, 55, 55),
            PressedColor = Color.FromArgb(70, 70, 70),
            SelectedColor = Color.FromArgb(0, 90, 158),
            ForeColor = Color.White,
            IconPadding = 4,
            IconPainter = icon,
            AccessibleName = tooltip,
            Cursor = interactive ? Cursors.Hand : Cursors.Default,
            TabStop = false,
            Margin = new Padding(1, 3, 1, 0)
        };
        _toolTip.SetToolTip(button, tooltip);
        return button;
    }

    private void AddSeparator()
    {
        var separator = new Panel
        {
            Width = 1,
            Height = 28,
            BackColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(4, 4, 4, 0)
        };
        _content.Controls.Add(separator);
    }

    private void AddTrackBar(
        string tooltip,
        int minimum,
        int maximum,
        int value,
        Action<int> setter,
        int tickFrequency = 10,
        int width = 120)
    {
        var valueLabel = new Label
        {
            AutoSize = false,
            Width = 30,
            Height = 20,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 8, 0, 0)
        };
        _toolTip.SetToolTip(valueLabel, tooltip);

        var trackBar = new TrackBar
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            TickFrequency = tickFrequency,
            SmallChange = 1,
            LargeChange = tickFrequency,
            Width = width,
            Height = 32,
            AutoSize = false,
            Margin = new Padding(0, 1, 3, 0),
            BackColor = Color.FromArgb(30, 30, 30),
            AccessibleName = tooltip
        };
        _toolTip.SetToolTip(trackBar, tooltip);
        valueLabel.Text = trackBar.Value.ToString();
        trackBar.ValueChanged += (_, _) =>
        {
            setter(trackBar.Value);
            valueLabel.Text = trackBar.Value.ToString();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        };
        _content.Controls.Add(trackBar);
        _content.Controls.Add(valueLabel);
    }

    private void AddToggle(
        Action<Graphics, Rectangle, Color> icon,
        string tooltip,
        Func<bool> selected,
        Action setter)
    {
        var button = CreateIconButton(icon, tooltip, interactive: true);
        button.Click += (_, _) =>
        {
            setter();
            RefreshToggles();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        };
        _toggleButtons.Add((button, selected));
        _content.Controls.Add(button);
    }

    private void RefreshToggles()
    {
        foreach (var toggle in _toggleButtons)
        {
            toggle.Button.IsSelected = toggle.IsSelected();
            toggle.Button.Invalidate();
        }
    }

    private void FitToContent()
    {
        var width = _content.Padding.Horizontal;
        var height = _content.Padding.Vertical;
        foreach (Control control in _content.Controls)
        {
            width += control.Width + control.Margin.Horizontal;
            height = Math.Max(
                height,
                _content.Padding.Vertical + control.Height + control.Margin.Vertical);
        }

        Width = Math.Max(44, width + 2);
        Height = Math.Max(40, height + 2);
    }
}
