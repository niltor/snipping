using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Snipping.Core.Export;
using Snipping.Core.Ocr;
using Snipping.Core.Settings;

namespace Snipping.App;

public sealed record PinRequestInfo(Bitmap Bitmap, Point ScreenLocation);

internal enum ToolbarPlacement
{
    BelowSelection,
    AboveSelection,
    InsideSelection
}

internal readonly record struct ToolbarLayout(Point Location, ToolbarPlacement Placement);

public sealed class DesktopSnippingOverlayForm : Form
{
    private const int MinSelection = 2;
    private const int SmartDragThreshold = 6;

    private enum Phase { Selecting, Ready, Drawing }

    private enum ResizeHandle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Move }

    // Dependencies
    private readonly SnippingSettings _settings;
    private readonly ExportManager _exportManager;
    private readonly IOcrService _ocrService;
    private readonly FeatureEntitlements _features;

    // Screen capture (never modified — annotations are rendered as overlays)
    private Bitmap? _screenBitmap;

    // Selection state (all coordinates in form‑local = bitmap‑pixel space)
    private Phase _phase = Phase.Selecting;
    private Point? _selStart;
    private Rectangle _selection;
    private readonly SmartSelectionDetector _smartSelectionDetector = new();
    private Rectangle _smartCandidate;
    private bool _smartCandidateIsFallback;
    private bool _smartCandidateNeedsRefinement;
    private SmartSelectionSource _smartCandidateSource = SmartSelectionSource.WindowFallback;
    private int _smartCandidateConfidence;
    private Rectangle _smartPressedCandidate;
    private Point _smartPressPoint;
    private SmartSelectionWorker? _smartSelectionWorker;

    // Selection resize state
    private ResizeHandle _activeHandle = ResizeHandle.None;
    private Point _resizeOrigin;
    private Rectangle _resizeOriginalSel;

    // Annotation state
    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private Color _color = Color.Red;
    private readonly List<AnnotationItem> _annotations = [];
    private Point? _drawStart;
    private Point _drawEnd;
    private List<Point>? _freePoints;
    private readonly Dictionary<AnnotationTool, AnnotationToolOptions> _toolOptions = new()
    {
        [AnnotationTool.Rectangle] = new(),
        [AnnotationTool.Ellipse] = new(),
        [AnnotationTool.Arrow] = new(),
        [AnnotationTool.Line] = new(),
        [AnnotationTool.Text] = new(),
        [AnnotationTool.Mosaic] = new()
    };

    // Inline text input
    private TextBox? _textBox;
    private Panel? _textPanel;
    private Point _textInputPosition;
    private float _textFontSize = 18f;
    private bool _textInputBold;
    private bool _textInputItalic;
    private int _editingAnnotationIndex = -1;
    private bool _textInputManualSize;
    private bool _textInputDragging;
    private bool _textInputResizing;
    private Point _textInputMouseOriginScreen;
    private Point _textInputPanelOrigin;
    private Size _textInputPanelSizeOrigin;

    // Mouse tracking for magnifier
    private Point _lastMousePt;

    // Toolbar
    private readonly RoundedPanel _toolbar;
    private readonly AnnotationOptionsPanel _optionsPanel;
    private readonly List<RoundedButton> _toolBtns = [];
    private readonly List<RoundedButton> _colorBtns = [];
    private RoundedButton? _ocrButton;
    private readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 200, AutoPopDelay = 4000 };

    // OCR state is tied to the current selection.
    private OcrResultPanel? _ocrPanel;
    private IReadOnlyList<OcrTextLine> _ocrLines = Array.Empty<OcrTextLine>();
    private CancellationTokenSource? _ocrCancellation;
    private int _selectedOcrLine = -1;
    private bool _ocrRunning;
    private bool _ocrMode;

    // Pin result (set when user presses Ctrl+T)
    public PinRequestInfo? PinResult { get; private set; }

    // Pin shortcut parsed
    private readonly bool _pinCtrl, _pinShift, _pinAlt;
    private readonly Keys _pinKey;

    public DesktopSnippingOverlayForm(SnippingSettings settings, ExportManager exportManager, IOcrService ocrService)
        : this(settings, exportManager, ocrService, new FeatureEntitlements())
    {
    }

    internal DesktopSnippingOverlayForm(
        SnippingSettings settings,
        ExportManager exportManager,
        IOcrService ocrService,
        FeatureEntitlements features)
    {
        _settings = settings;
        _exportManager = exportManager;
        _ocrService = ocrService;
        _features = features;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;

        _toolbar = new RoundedPanel
        {
            Visible = false,
            Height = 46,
            BackColor = Color.FromArgb(30, 30, 30),
            CornerRadius = 10,
            BorderColor = Color.FromArgb(80, 80, 80),
            BorderThickness = 1,
            // Keep the operation bar visually stable over any captured content.
            TintColor = Color.FromArgb(255, 30, 30, 30)
        };
        _optionsPanel = new AnnotationOptionsPanel(_settings.Language)
        {
            Visible = false
        };
        _optionsPanel.OptionsChanged += (_, _) =>
        {
            ApplyTextOptionsToInput();
            Invalidate();
        };
        BuildToolbar();
        Controls.Add(_toolbar);
        Controls.Add(_optionsPanel);

        ParseShortcut(_settings.PinShortcut, out _pinCtrl, out _pinShift, out _pinAlt, out _pinKey);
        _smartSelectionWorker = new SmartSelectionWorker(
            _smartSelectionDetector,
            ApplySmartCandidateFromWorker);
    }

    private static void ParseShortcut(string shortcut, out bool ctrl, out bool shift, out bool alt, out Keys key)
    {
        ctrl = shift = alt = false;
        key = Keys.None;
        foreach (var part in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else Enum.TryParse(part, true, out key);
        }
    }

    #region Toolbar construction

    private void BuildToolbar()
    {
        var x = 6;
        const int iconSize = 34;
        const int btnY = 4;
        const int gap = 2;
        const int groupGap = 6;

        // ── Tool icon buttons ─────────────────────────────────
        var tools = new (AnnotationTool tool, Action<Graphics, Rectangle, Color> icon)[]
        {
            (AnnotationTool.Rectangle, ToolIcons.Rectangle),
            (AnnotationTool.Ellipse, ToolIcons.Ellipse),
            (AnnotationTool.Arrow, ToolIcons.Arrow),
            (AnnotationTool.Line, ToolIcons.Line),
            (AnnotationTool.Text, ToolIcons.Text),
            (AnnotationTool.Highlight, ToolIcons.Highlight),
            (AnnotationTool.Mosaic, ToolIcons.Mosaic),
            (AnnotationTool.FreeDraw, ToolIcons.FreeDraw),
        };

        foreach (var (tool, icon) in tools)
        {
            var tip = UiText.AnnotationToolTip(_settings.Language, tool);
            var btn = new RoundedButton
            {
                Location = new Point(x, btnY),
                Size = new Size(iconSize, iconSize),
                ForeColor = Color.White,
                CornerRadius = 6,
                IdleColor = Color.FromArgb(38, 38, 38),
                HoverColor = Color.FromArgb(55, 55, 55),
                PressedColor = Color.FromArgb(70, 70, 70),
                SelectedColor = Color.FromArgb(0, 90, 158),
                IconPadding = tool == AnnotationTool.Text ? 0 : 1,
                AccessibleName = tip,
                IconPainter = icon
            };
            _tip.SetToolTip(btn, tip);
            var captured = tool;
            btn.Click += (_, _) => SetTool(captured);
            _toolBtns.Add(btn);
            _toolbar.Controls.Add(btn);
            x += iconSize + gap;
        }

        x += groupGap;
        _toolbar.Controls.Add(MakeSep(x, iconSize, btnY));
        x += 1 + groupGap;

        // ── Color dot buttons ─────────────────────────────────
        const int colorSize = 24;
        Color[] colors = [
            Color.FromArgb(235, 64, 52),
            Color.FromArgb(0, 122, 255),
            Color.FromArgb(52, 199, 89),
            Color.FromArgb(255, 214, 10),
            Color.White
        ];

        for (var i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            var btn = new RoundedButton
            {
                Location = new Point(x, btnY + (iconSize - colorSize) / 2),
                Size = new Size(colorSize, colorSize),
                CornerRadius = 4, // slightly rounded square
                IdleColor = c,
                HoverColor = c,
                PressedColor = c,
                SelectedColor = c,
                ForeColor = c,
                SelectedBorderColor = Color.White,
                SelectedBorderWidth = 2,
                AccessibleName = UiText.ColorToolTip(_settings.Language, c, i + 1)
            };
            _tip.SetToolTip(btn, UiText.ColorToolTip(_settings.Language, c, i + 1));
            var captured = c;
            btn.Click += (_, _) => SetColor(captured);
            _colorBtns.Add(btn);
            _toolbar.Controls.Add(btn);
            x += colorSize + 4;
        }

        x += groupGap;
        _toolbar.Controls.Add(MakeSep(x, iconSize, btnY));
        x += 1 + groupGap;

        // ── Undo icon button ──────────────────────────────────
        var undoBtn = new RoundedButton
        {
            Location = new Point(x, btnY),
            Size = new Size(iconSize, iconSize),
            ForeColor = Color.White,
            CornerRadius = 6,
            IdleColor = Color.FromArgb(38, 38, 38),
            HoverColor = Color.FromArgb(55, 55, 55),
            PressedColor = Color.FromArgb(70, 70, 70),
            IconPainter = ToolIcons.Undo,
            AccessibleName = UiText.UndoToolTip(_settings.Language)
        };
        _tip.SetToolTip(undoBtn, UiText.UndoToolTip(_settings.Language));
        undoBtn.Click += (_, _) => Undo();
        _toolbar.Controls.Add(undoBtn);
        x += iconSize + groupGap;

        _toolbar.Controls.Add(MakeSep(x, iconSize, btnY));
        x += 1 + groupGap;

        // ── Action icon buttons ───────────────────────────────
        var actions = new (string tip, Action<Graphics, Rectangle, Color> icon, Func<Task> handler, bool isOcr)[]
        {
            (UiText.OcrToolTip(_settings.Language), ToolIcons.Ocr, StartOcrAsync, true),
            (UiText.PinToolTip(_settings.Language, _settings.PinShortcut), ToolIcons.Pin, () => { PinAndClose(); return Task.CompletedTask; }, false),
            (UiText.SaveToolTip(_settings.Language), ToolIcons.Save, SaveAndCloseAsync, false),
            (UiText.CopyToolTip(_settings.Language), ToolIcons.Copy, () => { CopyAndClose(); return Task.CompletedTask; }, false),
            (UiText.CloseToolTip(_settings.Language), ToolIcons.Close, () => { Close(); return Task.CompletedTask; }, false),
        };

        foreach (var (tip, icon, handler, isOcr) in actions)
        {
            var btn = new RoundedButton
            {
                Location = new Point(x, btnY),
                Size = new Size(iconSize, iconSize),
                ForeColor = Color.White,
                CornerRadius = 6,
                IdleColor = Color.FromArgb(38, 38, 38),
                HoverColor = Color.FromArgb(55, 55, 55),
                PressedColor = Color.FromArgb(70, 70, 70),
                AccessibleName = tip,
                IconPainter = icon
            };
            if (isOcr)
                _ocrButton = btn;
            _tip.SetToolTip(btn, tip);
            var h = handler;
            btn.Click += (_, _) => _ = h();
            _toolbar.Controls.Add(btn);
            x += iconSize + gap;
        }

        x += 4;
        _toolbar.Width = x;
        _toolbar.Height = iconSize + btnY * 2;

        UpdateToolHighlight();
        UpdateColorHighlight();
    }

    private static Panel MakeSep(int x, int iconSize, int btnY) =>
        new()
        {
            Location = new Point(x, btnY + 4),
            Size = new Size(1, iconSize - 6),
            BackColor = Color.FromArgb(80, 80, 80)
        };

    private void SetTool(AnnotationTool tool)
    {
        CommitTextInput();
        _ocrMode = false;
        ClearOcrResults();
        _tool = tool;
        UpdateToolHighlight();
        UpdateOptionsPanel();
        UpdateCursor();
    }

    private void SetColor(Color c)
    {
        _color = c;
        UpdateColorHighlight();
        if (_textBox is not null)
            _textBox.ForeColor = _color;
    }

    private void UpdateToolHighlight()
    {
        var allTools = Enum.GetValues<AnnotationTool>();
        for (var i = 0; i < _toolBtns.Count && i < allTools.Length; i++)
        {
            _toolBtns[i].IsSelected = !_ocrMode && allTools[i] == _tool;
            _toolBtns[i].Invalidate();
        }
    }

    private void UpdateColorHighlight()
    {
        foreach (var btn in _colorBtns)
        {
            var selected = btn.IdleColor.ToArgb() == _color.ToArgb();
            // Draw a white ring around selected color
            btn.IsSelected = selected;
            btn.Invalidate();
        }
    }

    private void UpdateCursor()
    {
        if (_ocrMode)
        {
            Cursor = Cursors.Default;
            return;
        }

        if (_phase == Phase.Selecting)
        {
            Cursor = Cursors.Cross;
        }
        else if (_phase == Phase.Ready)
        {
            Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    #endregion

    #region Form lifecycle

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_screenBitmap is null)
            PrepareScreenCapture();
        Invalidate();
    }

    internal void PrepareScreenCapture()
    {
        _screenBitmap?.Dispose();
        var vs = SystemInformation.VirtualScreen;
        _screenBitmap = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_screenBitmap))
            g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
        _smartSelectionDetector.SetCaptureSurface(_screenBitmap, vs.Location);
        // Build the native window/control catalog before this form is shown.
        // The overlay is therefore absent from the catalog and mouse movement
        // can use local rectangle hit-testing instead of EnumWindows/UIA calls.
        _smartSelectionDetector.RefreshNativeSnapshot();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearOcrResults();
            _smartSelectionDetector.SetCaptureSurface(null, Point.Empty);
            _screenBitmap?.Dispose();
            _textPanel?.Dispose();
            _textBox?.Dispose();
            _smartSelectionWorker?.Dispose();
            _smartSelectionWorker = null;
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion

    #region Painting (layer‑based: screen → overlay → selection clear → annotations → preview → border)

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_screenBitmap is null) return;

        var g = e.Graphics;
        var paintBounds = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
        if (paintBounds.Width < 1 || paintBounds.Height < 1)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1. Paint only the invalid region. This matters on 4K and virtual
        // multi-monitor desktops because candidate updates invalidate a tiny
        // union rather than the whole overlay.
        g.DrawImage(_screenBitmap, paintBounds, paintBounds, GraphicsUnit.Pixel);

        // 2. Dark overlay
        using (var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillRectangle(brush, paintBounds);

        if (_selection.Width < MinSelection || _selection.Height < MinSelection)
        {
            DrawSmartCandidate(g);
            return;
        }

        // 3. Clear selection area — reveal the original capture
        var saved = g.Save();
        g.SetClip(_selection, CombineMode.Intersect);
        var selectionPaint = Rectangle.Intersect(_selection, paintBounds);
        if (selectionPaint.Width > 0 && selectionPaint.Height > 0)
            g.DrawImage(_screenBitmap, selectionPaint, selectionPaint, GraphicsUnit.Pixel);

        // 4. Committed annotations
        foreach (var ann in _annotations)
            ann.Draw(g, _screenBitmap);

        // 5. In‑progress preview
        DrawPreview(g);

        // 5b. OCR line selection overlays
        DrawOcrHighlights(g);

        g.Restore(saved);

        // 6. Selection border
        using (var pen = new Pen(Color.FromArgb(0, 174, 255), 2))
            g.DrawRectangle(pen, _selection.X, _selection.Y, _selection.Width, _selection.Height);

        // 6b. Selection resize handles
        if (_phase == Phase.Ready)
            DrawSelectionHandles(g);

        // 7. Dimension label
        DrawSizeLabel(g);

        // 8. Toolbar shadow
        if (_toolbar.Visible)
            DrawToolbarShadow(g);

        // 9. Magnifier (during active selection drag)
        if (_selStart is not null)
            DrawMagnifier(g, _lastMousePt);
    }

    private void DrawSizeLabel(Graphics g)
    {
        var text = $"{_selection.Width} × {_selection.Height}";
        using var font = new Font("Microsoft YaHei UI", 9f);
        var sz = g.MeasureString(text, font);
        var lx = (float)_selection.Left;
        var ly = _selection.Top - sz.Height - 12;
        if (ly < 2) ly = _selection.Top + 8;

        var rect = new RectangleF(lx, ly, sz.Width + 16, sz.Height + 6);
        using var path = new GraphicsPath();
        var d = 8;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        using (var bg = new SolidBrush(Color.FromArgb(220, 32, 32, 32)))
            g.FillPath(bg, path);
        using (var fg = new SolidBrush(Color.White))
            g.DrawString(text, font, fg, lx + 8, ly + 3);
    }

    private void DrawSmartCandidate(Graphics g)
    {
        if (_smartCandidate.Width < MinSelection || _smartCandidate.Height < MinSelection)
            return;

        var saved = g.Save();
        g.SetClip(_smartCandidate, CombineMode.Intersect);
        g.DrawImage(_screenBitmap!, _smartCandidate, _smartCandidate, GraphicsUnit.Pixel);
        g.Restore(saved);

        using var pen = new Pen(Color.FromArgb(0, 174, 255), 2);
        g.DrawRectangle(pen, _smartCandidate.X, _smartCandidate.Y, _smartCandidate.Width, _smartCandidate.Height);
    }

    private void DrawPreview(Graphics g)
    {
        if (_drawStart is null || _phase != Phase.Drawing) return;

        var s = _drawStart.Value;
        var end = _drawEnd;

        switch (_tool)
        {
            case AnnotationTool.Rectangle:
            {
                new RectangleAnnotation
                {
                    Start = s,
                    End = end,
                    Color = _color,
                    Opacity = GetToolOptions(AnnotationTool.Rectangle).ShapeOpacity,
                    RenderMode = GetToolOptions(AnnotationTool.Rectangle).ShapeMode
                }.Draw(g, _screenBitmap);
                break;
            }
            case AnnotationTool.Ellipse:
            {
                new EllipseAnnotation
                {
                    Start = s,
                    End = end,
                    Color = _color,
                    Opacity = GetToolOptions(AnnotationTool.Ellipse).ShapeOpacity,
                    RenderMode = GetToolOptions(AnnotationTool.Ellipse).ShapeMode
                }.Draw(g, _screenBitmap);
                break;
            }
            case AnnotationTool.Arrow:
            {
                new ArrowAnnotation
                {
                    Start = s,
                    End = end,
                    Color = _color,
                    ArrowHead = GetToolOptions(AnnotationTool.Arrow).ArrowHead,
                    StrokeStyle = GetToolOptions(AnnotationTool.Arrow).ArrowStrokeStyle
                }.Draw(g, _screenBitmap);
                break;
            }
            case AnnotationTool.Line:
            {
                new LineAnnotation
                {
                    Start = s,
                    End = end,
                    Color = _color,
                    StrokeStyle = GetToolOptions(AnnotationTool.Line).LineStyle
                }.Draw(g, _screenBitmap);
                break;
            }
            case AnnotationTool.Highlight:
            {
                var r = AnnotationHelper.Normalize(s, end);
                if (r.Width > 0 && r.Height > 0)
                {
                    using var brush = new SolidBrush(Color.FromArgb(100, Color.Yellow));
                    g.FillRectangle(brush, r);
                }

                break;
            }
            case AnnotationTool.Mosaic:
            {
                if (_screenBitmap is not null && _freePoints is { Count: >= 2 })
                {
                    new MosaicBrushAnnotation
                    {
                        Points = new List<Point>(_freePoints),
                        BrushWidth = GetToolOptions(AnnotationTool.Mosaic).MosaicBrushWidth
                    }.Draw(g, _screenBitmap);
                }
                break;
            }
            case AnnotationTool.FreeDraw:
            {
                if (_freePoints is { Count: >= 2 })
                {
                    using var pen = new Pen(_color, 3)
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    g.DrawLines(pen, _freePoints.ToArray());
                }

                break;
            }
        }
    }

    #endregion

    #region Mouse interaction

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;

        var pt = e.Location;

        switch (_phase)
        {
            case Phase.Selecting:
                _ocrMode = false;
                ClearOcrResults();

                if (_smartCandidate.Width >= MinSelection
                    && _smartCandidate.Height >= MinSelection
                    && _smartCandidate.Contains(pt))
                {
                    _smartPressedCandidate = _smartCandidate;
                    _smartPressPoint = pt;
                    _smartSelectionWorker?.CancelPending();
                    return;
                }

                _smartPressedCandidate = Rectangle.Empty;
                _smartCandidate = Rectangle.Empty;
                _smartCandidateIsFallback = false;
                _smartCandidateNeedsRefinement = false;
                _smartCandidateSource = SmartSelectionSource.WindowFallback;
                _smartCandidateConfidence = 0;
                _smartSelectionWorker?.CancelPending();
                _selStart = pt;
                _selection = Rectangle.Empty;
                _toolbar.Visible = false;
                _optionsPanel.Visible = false;
                break;

            case Phase.Ready:
            {
                // Check resize handles first
                var handle = HitTestHandle(pt);
                if (handle is not ResizeHandle.None and not ResizeHandle.Move)
                {
                    _ocrMode = false;
                    ClearOcrResults();
                    _activeHandle = handle;
                    _resizeOrigin = pt;
                    _resizeOriginalSel = _selection;
                    return;
                }

                // Check move (inside selection near edges / outside annotation area)
                if (handle == ResizeHandle.Move)
                {
                    _ocrMode = false;
                    ClearOcrResults();
                    _activeHandle = ResizeHandle.Move;
                    _resizeOrigin = pt;
                    _resizeOriginalSel = _selection;
                    return;
                }

                if (!_selection.Contains(pt))
                {
                    // Click outside → start new selection
                    _ocrMode = false;
                    ClearOcrResults();
                    _annotations.Clear();
                    _phase = Phase.Selecting;
                    _selStart = pt;
                    _selection = Rectangle.Empty;
                    _toolbar.Visible = false;
                    _optionsPanel.Visible = false;
                    Cursor = Cursors.Cross;
                    Invalidate();
                    return;
                }

                if (handle == ResizeHandle.None)
                {
                    var ocrIndex = HitTestOcrLine(pt);
                    if (ocrIndex >= 0)
                    {
                        SelectOcrLine(ocrIndex);
                        return;
                    }
                }

                if (_ocrMode)
                    return;

                if (_tool == AnnotationTool.Text)
                {
                    // Hit-test existing text annotations for re-editing
                    for (var i = _annotations.Count - 1; i >= 0; i--)
                    {
                        if (_annotations[i] is TextAnnotation ta && ta.GetBounds().Contains(pt))
                        {
                            _editingAnnotationIndex = i;
                            ShowTextInput(ta.Position, ta.Text, ta.FontSize, ta.Color, ta.Bold, ta.Italic);
                            return;
                        }
                    }

                    ShowTextInput(pt);
                    return;
                }

                _phase = Phase.Drawing;
                _drawStart = pt;
                _drawEnd = pt;
                if (_tool is AnnotationTool.FreeDraw or AnnotationTool.Mosaic)
                    _freePoints = [pt];
                break;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pt = e.Location;
        _lastMousePt = pt;

        if (_phase == Phase.Selecting && _selStart is null)
        {
            if (_smartPressedCandidate.Width >= MinSelection)
            {
                var dx = pt.X - _smartPressPoint.X;
                var dy = pt.Y - _smartPressPoint.Y;
                if (dx * dx + dy * dy > SmartDragThreshold * SmartDragThreshold)
                {
                    _selStart = _smartPressPoint;
                    _smartPressedCandidate = Rectangle.Empty;
                    _smartCandidate = Rectangle.Empty;
                    _smartCandidateIsFallback = false;
                    _smartCandidateNeedsRefinement = false;
                    _smartCandidateSource = SmartSelectionSource.WindowFallback;
                    _smartCandidateConfidence = 0;
                    _selection = AnnotationHelper.Normalize(_selStart.Value, pt);
                    Cursor = Cursors.Cross;
                    Invalidate();
                }
                else
                {
                    Cursor = Cursors.Cross;
                }

                return;
            }

            RequestSmartCandidate(pt);
            Cursor = Cursors.Cross;
            return;
        }

        if (_ocrMode && _phase == Phase.Ready)
        {
            Cursor = HitTestOcrLine(pt) >= 0 ? Cursors.Hand : Cursors.Default;
            return;
        }

        // Active resize / move
        if (_activeHandle != ResizeHandle.None)
        {
            ApplyResize(pt);
            PositionToolbar();
            Invalidate();
            return;
        }

        if (_selStart is not null)
        {
            _selection = AnnotationHelper.Normalize(_selStart.Value, pt);
            Invalidate();
            return;
        }

        if (_phase == Phase.Drawing && _drawStart is not null)
        {
            _drawEnd = pt;
            _freePoints?.Add(pt);
            Invalidate();
            return;
        }

        // Update cursor based on handle hit or tool
        if (_phase == Phase.Ready)
        {
            var handle = HitTestHandle(pt);
            if (handle != ResizeHandle.None)
            {
                Cursor = GetHandleCursor(handle);
            }
            else if (_tool == AnnotationTool.Text && _annotations.Any(a => a is TextAnnotation ta && ta.GetBounds().Contains(pt)))
            {
                Cursor = Cursors.IBeam;
            }
            else
            {
                Cursor = GetHandleCursor(handle);
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // Finish resize / move
        if (_activeHandle != ResizeHandle.None)
        {
            _activeHandle = ResizeHandle.None;
            if (_selection.Width >= MinSelection && _selection.Height >= MinSelection)
            {
                PositionToolbar();
                _toolbar.Visible = true;
                UpdateOptionsPanel();
            }
            Invalidate();
            return;
        }

        if (_smartPressedCandidate.Width >= MinSelection)
        {
            var candidate = _smartPressedCandidate;
            _smartPressedCandidate = Rectangle.Empty;
            _smartCandidate = Rectangle.Empty;
            _smartCandidateIsFallback = false;
            _smartCandidateNeedsRefinement = false;
            _smartCandidateSource = SmartSelectionSource.WindowFallback;
            _smartCandidateConfidence = 0;
            _selection = candidate;
            _phase = Phase.Ready;
            UpdateCursor();
            PositionToolbar();
            _toolbar.Visible = true;
            UpdateOptionsPanel();
            Invalidate();
            return;
        }

        if (_selStart is not null)
        {
            _selection = AnnotationHelper.Normalize(_selStart.Value, e.Location);
            _selStart = null;

            if (_selection.Width >= MinSelection && _selection.Height >= MinSelection)
            {
                _phase = Phase.Ready;
                UpdateCursor();
                PositionToolbar();
                _toolbar.Visible = true;
                UpdateOptionsPanel();
            }
            else
            {
                _selection = Rectangle.Empty;
            }

            Invalidate();
            return;
        }

        if (_phase == Phase.Drawing && _drawStart is not null)
        {
            _drawEnd = e.Location;
            CommitAnnotation();
            _drawStart = null;
            _freePoints = null;
            _phase = Phase.Ready;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Ctrl+Wheel adjusts font size when text input is active
        if (_textBox is not null && ModifierKeys.HasFlag(Keys.Control))
        {
            var delta = e.Delta > 0 ? 2f : -2f;
            _textFontSize = Math.Clamp(_textFontSize + delta, 10f, 100f);
            var textOptions = GetToolOptions(AnnotationTool.Text);
            textOptions.TextFontSize = (int)_textFontSize;
            _textInputBold = textOptions.TextBold;
            _textInputItalic = textOptions.TextItalic;
            _textBox.Font = new Font(
                "Microsoft YaHei UI",
                _textFontSize,
                AnnotationHelper.GetFontStyle(textOptions.TextBold, textOptions.TextItalic),
                GraphicsUnit.Pixel);
            _textBox.ForeColor = _color;
            AutoSizeTextPanel();
        }
    }

    #endregion

    #region Smart selection

    private void RequestSmartCandidate(Point clientPoint)
    {
        if (_screenBitmap is null
            || !IsHandleCreated
            || _phase != Phase.Selecting
            || _selStart is not null
            || (!_smartCandidateIsFallback
                && !_smartCandidateNeedsRefinement
                && _smartCandidate.Contains(clientPoint)))
            return;

        _smartSelectionWorker?.Submit(PointToScreen(clientPoint), Handle);
    }

    private void ApplySmartCandidateFromWorker(SmartSelectionResult result)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing
                    || _phase != Phase.Selecting
                    || _selStart is not null)
                {
                    return;
                }

                if (result.IsRefinement
                    && (result.Candidate is null
                        || (result.Candidate.IsWindowFallback && !_smartCandidateIsFallback)))
                {
                    return;
                }

                var currentScreenPoint = Cursor.Position;
                if (ToSmartGrid(currentScreenPoint) != ToSmartGrid(result.ScreenPoint))
                    return;

                if (result.Candidate is not null
                    && !ShouldApplySmartCandidate(
                        result.Candidate,
                        _smartCandidate,
                        _smartCandidateIsFallback,
                        _smartCandidateSource,
                        _smartCandidateConfidence,
                        _smartCandidate.Contains(PointToClient(currentScreenPoint))))
                {
                    return;
                }

                var previous = _smartCandidate;
                _smartCandidate = result.Candidate is null
                    ? Rectangle.Empty
                    : ScreenToClientRectangle(result.Candidate.ScreenBounds);
                _smartCandidateIsFallback = result.Candidate?.IsWindowFallback == true;
                _smartCandidateNeedsRefinement = result.Candidate?.NeedsRefinement == true;
                _smartCandidateSource = result.Candidate?.Source ?? SmartSelectionSource.WindowFallback;
                _smartCandidateConfidence = result.Candidate?.Confidence ?? 0;
                Cursor = Cursors.Cross;

                var dirty = Rectangle.Union(previous, _smartCandidate);
                dirty.Inflate(4, 4);
                Invalidate(Rectangle.Intersect(dirty, ClientRectangle));
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The overlay can close between the worker result and BeginInvoke.
        }
    }

    internal static bool ShouldApplySmartCandidate(
        SmartSelectionCandidate incoming,
        Rectangle currentBounds,
        bool currentIsFallback,
        SmartSelectionSource currentSource,
        int currentConfidence,
        bool pointerInsideCurrent)
    {
        if (currentBounds.Width < MinSelection
            || currentBounds.Height < MinSelection
            || !pointerInsideCurrent)
        {
            return true;
        }

        // Never regress from a real control/visual candidate to a whole-window
        // fallback while the pointer is still inside the existing candidate.
        if (incoming.IsWindowFallback && !currentIsFallback)
            return false;
        if (!incoming.IsWindowFallback && currentIsFallback)
            return true;

        var currentArea = (long)currentBounds.Width * currentBounds.Height;
        var incomingArea = (long)incoming.ScreenBounds.Width * incoming.ScreenBounds.Height;
        if (currentArea <= 0)
            return true;

        // A broad container must not displace an already selected leaf merely
        // because the container was returned faster.
        if (incoming.IsContainer
            && currentSource != SmartSelectionSource.WindowFallback
            && currentConfidence >= incoming.Confidence)
        {
            return false;
        }

        if (incomingArea > currentArea * 1.25
            && incoming.Confidence <= currentConfidence)
        {
            return false;
        }

        return incoming.Confidence + 12 >= currentConfidence
            || incomingArea < currentArea * 0.80;
    }

    private void UpdateOptionsPanel()
    {
        AnnotationToolOptions? options = null;
        var show = _features.AnnotationEnhancementsEnabled
            && _toolbar.Visible
            && AnnotationOptionsPanel.Supports(_tool)
            && _toolOptions.TryGetValue(_tool, out options);

        _optionsPanel.Visible = show;
        if (show)
        {
            _optionsPanel.Bind(_tool, options!);
            PositionToolbar();
            _optionsPanel.BringToFront();
        }
    }

    private AnnotationToolOptions GetToolOptions(AnnotationTool tool)
    {
        if (_toolOptions.TryGetValue(tool, out var options))
            return options;

        return new AnnotationToolOptions();
    }

    private void ApplyTextOptionsToInput()
    {
        if (_textBox is null || _tool != AnnotationTool.Text)
            return;

        var options = GetToolOptions(AnnotationTool.Text);
        options.Normalize();
        _textFontSize = options.TextFontSize;
        _textInputBold = options.TextBold;
        _textInputItalic = options.TextItalic;
        _textBox.Font = new Font(
            "Microsoft YaHei UI",
            _textFontSize,
            AnnotationHelper.GetFontStyle(options.TextBold, options.TextItalic),
            GraphicsUnit.Pixel);
        _textBox.ForeColor = _color;
        AutoSizeTextPanel();
    }

    private Rectangle ScreenToClientRectangle(Rectangle screenBounds)
    {
        var topLeft = PointToClient(new Point(screenBounds.Left, screenBounds.Top));
        var bottomRight = PointToClient(new Point(screenBounds.Right, screenBounds.Bottom));
        var clientBounds = Rectangle.FromLTRB(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
        return Rectangle.Intersect(clientBounds, ClientRectangle);
    }

    private static Point ToSmartGrid(Point screenPoint) => new(
        (int)Math.Floor(screenPoint.X / 6d),
        (int)Math.Floor(screenPoint.Y / 6d));

    #endregion

    #region Selection resize / move

    private ResizeHandle HitTestHandle(Point pt)
    {
        if (_selection.Width < MinSelection || _selection.Height < MinSelection)
            return ResizeHandle.None;

        const int ht = 8;
        var s = _selection;

        var handles = new (ResizeHandle h, Point c)[]
        {
            (ResizeHandle.TopLeft,     new(s.Left, s.Top)),
            (ResizeHandle.Top,         new(s.Left + s.Width / 2, s.Top)),
            (ResizeHandle.TopRight,    new(s.Right, s.Top)),
            (ResizeHandle.Right,       new(s.Right, s.Top + s.Height / 2)),
            (ResizeHandle.BottomRight, new(s.Right, s.Bottom)),
            (ResizeHandle.Bottom,      new(s.Left + s.Width / 2, s.Bottom)),
            (ResizeHandle.BottomLeft,  new(s.Left, s.Bottom)),
            (ResizeHandle.Left,        new(s.Left, s.Top + s.Height / 2)),
        };

        foreach (var (h, c) in handles)
        {
            if (Math.Abs(pt.X - c.X) <= ht && Math.Abs(pt.Y - c.Y) <= ht)
                return h;
        }

        // Near edge (within 5px of border) → move
        var inner = Rectangle.Inflate(_selection, -5, -5);
        if (_selection.Contains(pt) && !inner.Contains(pt))
            return ResizeHandle.Move;

        return ResizeHandle.None;
    }

    private Cursor GetHandleCursor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
        ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
        ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
        ResizeHandle.Move => Cursors.SizeAll,
        _ => _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross
    };

    private void ApplyResize(Point pt)
    {
        var orig = _resizeOriginalSel;

        if (_activeHandle == ResizeHandle.Move)
        {
            var dx = pt.X - _resizeOrigin.X;
            var dy = pt.Y - _resizeOrigin.Y;
            _selection = new Rectangle(orig.X + dx, orig.Y + dy, orig.Width, orig.Height);
            return;
        }

        var left = orig.Left;
        var top = orig.Top;
        var right = orig.Right;
        var bottom = orig.Bottom;

        switch (_activeHandle)
        {
            case ResizeHandle.TopLeft:     left = pt.X; top = pt.Y; break;
            case ResizeHandle.Top:         top = pt.Y; break;
            case ResizeHandle.TopRight:    right = pt.X; top = pt.Y; break;
            case ResizeHandle.Right:       right = pt.X; break;
            case ResizeHandle.BottomRight: right = pt.X; bottom = pt.Y; break;
            case ResizeHandle.Bottom:      bottom = pt.Y; break;
            case ResizeHandle.BottomLeft:  left = pt.X; bottom = pt.Y; break;
            case ResizeHandle.Left:        left = pt.X; break;
        }

        _selection = new Rectangle(
            Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }

    #endregion

    #region Keyboard shortcuts

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Let active text editors handle their own input. In particular,
        // Ctrl+A must remain "select all" inside the OCR result text box.
        if (_textBox is not null || _ocrPanel?.IsTextEditorFocused == true) return;

        if (e.KeyCode == Keys.Escape) { Close(); return; }
        if (e.Control && e.KeyCode == Keys.Z) { Undo(); return; }
        if (e.Control && e.KeyCode == Keys.C && _ocrPanel is not null)
        {
            if (!_ocrPanel.IsTextEditorFocused)
            {
                _ocrPanel.CopySelected();
                return;
            }
        }
        if (e.Control && e.KeyCode == Keys.S && _phase == Phase.Ready) { _ = SaveAndCloseAsync(); return; }
        if (e.KeyCode == Keys.Enter && _phase == Phase.Ready) { CopyAndClose(); return; }
        if (e.Control && e.KeyCode == Keys.A && _phase == Phase.Ready && !_ocrRunning)
        {
            _ = StartOcrAsync();
            return;
        }
        if (e.Control == _pinCtrl && e.Shift == _pinShift && e.Alt == _pinAlt && e.KeyCode == _pinKey && _phase == Phase.Ready)
        {
            PinAndClose();
            return;
        }

        // Single‑key tool shortcuts
        switch (e.KeyCode)
        {
            case Keys.Q: SetTool(AnnotationTool.Rectangle); break;
            case Keys.W: SetTool(AnnotationTool.Ellipse); break;
            case Keys.E: SetTool(AnnotationTool.Arrow); break;
            case Keys.R: SetTool(AnnotationTool.Line); break;
            case Keys.T: SetTool(AnnotationTool.Text); break;
            case Keys.A: SetTool(AnnotationTool.Highlight); break;
            case Keys.S: SetTool(AnnotationTool.Mosaic); break;
            case Keys.D: SetTool(AnnotationTool.FreeDraw); break;
        }
    }

    #endregion

    #region Annotation commit / undo

    private void CommitAnnotation()
    {
        if (_drawStart is null) return;
        var s = _drawStart.Value;
        var end = _drawEnd;

        AnnotationItem? item = _tool switch
        {
            AnnotationTool.Rectangle => new RectangleAnnotation
            {
                Start = s,
                End = end,
                Color = _color,
                Opacity = GetToolOptions(AnnotationTool.Rectangle).ShapeOpacity,
                RenderMode = GetToolOptions(AnnotationTool.Rectangle).ShapeMode
            },
            AnnotationTool.Ellipse => new EllipseAnnotation
            {
                Start = s,
                End = end,
                Color = _color,
                Opacity = GetToolOptions(AnnotationTool.Ellipse).ShapeOpacity,
                RenderMode = GetToolOptions(AnnotationTool.Ellipse).ShapeMode
            },
            AnnotationTool.Arrow => new ArrowAnnotation
            {
                    Start = s,
                    End = end,
                    Color = _color,
                    ArrowHead = GetToolOptions(AnnotationTool.Arrow).ArrowHead,
                    StrokeStyle = GetToolOptions(AnnotationTool.Arrow).ArrowStrokeStyle
            },
            AnnotationTool.Line => new LineAnnotation
            {
                Start = s,
                End = end,
                Color = _color,
                StrokeStyle = GetToolOptions(AnnotationTool.Line).LineStyle
            },
            AnnotationTool.Highlight => new HighlightAnnotation { Start = s, End = end },
            AnnotationTool.Mosaic when _freePoints is { Count: >= 2 } =>
                new MosaicBrushAnnotation
                {
                    Points = new List<Point>(_freePoints),
                    BrushWidth = GetToolOptions(AnnotationTool.Mosaic).MosaicBrushWidth
                },
            AnnotationTool.FreeDraw when _freePoints is { Count: >= 2 } =>
                new FreeDrawAnnotation { Points = new List<Point>(_freePoints), Color = _color },
            _ => null
        };

        if (item is not null) _annotations.Add(item);
    }

    private void Undo()
    {
        if (_annotations.Count > 0)
        {
            _annotations.RemoveAt(_annotations.Count - 1);
            Invalidate();
        }
    }

    #endregion

    #region Text input

    private void ShowTextInput(
        Point localPt,
        string? existingText = null,
        float? fontSize = null,
        Color? color = null,
        bool? bold = null,
        bool? italic = null)
    {
        CommitTextInput();

        _textInputPosition = localPt;
        _textInputManualSize = false;
        var textOptions = GetToolOptions(AnnotationTool.Text);
        textOptions.Normalize();
        _textFontSize = fontSize.HasValue
            ? Math.Clamp(fontSize.Value, 10f, 100f)
            : textOptions.TextFontSize;
        _textInputBold = bold ?? textOptions.TextBold;
        _textInputItalic = italic ?? textOptions.TextItalic;
        if (color.HasValue) _color = color.Value;

        var font = new Font(
            "Microsoft YaHei UI",
            _textFontSize,
            AnnotationHelper.GetFontStyle(_textInputBold, _textInputItalic),
            GraphicsUnit.Pixel);
        var initialWidth = 40;
        if (!string.IsNullOrEmpty(existingText))
        {
            var sz = TextRenderer.MeasureText(existingText, font);
            initialWidth = sz.Width + 16;
        }

        var lineHeight = font.Height + 4;

        _textPanel = new Panel
        {
            Location = new Point(localPt.X - 1, localPt.Y - 1),
            Size = new Size(initialWidth + 2, lineHeight + 2),
            BackColor = Color.White,
            Padding = new Padding(1),
        };
        _textPanel.Paint += TextPanelOnPaint;
        _textPanel.MouseDown += TextInputMouseDown;
        _textPanel.MouseMove += TextInputMouseMove;
        _textPanel.MouseUp += TextInputMouseUp;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = font,
            ForeColor = _color,
            BackColor = Color.FromArgb(40, 40, 40),
            BorderStyle = BorderStyle.None,
            Cursor = Cursors.IBeam,
            Multiline = true,
            WordWrap = false,
            Text = existingText ?? "",
            ScrollBars = ScrollBars.None
        };

        _textBox.TextChanged += (_, _) => AutoSizeTextPanel();
        _textBox.MouseDown += TextInputMouseDown;
        _textBox.MouseMove += TextInputMouseMove;
        _textBox.MouseUp += TextInputMouseUp;
        _textBox.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Escape)
            {
                ke.SuppressKeyPress = true;
                CommitTextInput();
            }
        };

        _textPanel.Controls.Add(_textBox);
        Controls.Add(_textPanel);
        _textPanel.BringToFront();
        _textBox.Focus();
        if (!string.IsNullOrEmpty(existingText))
            _textBox.SelectionStart = existingText.Length;
    }

    private void AutoSizeTextPanel()
    {
        if (_textBox is null || _textPanel is null || _textInputManualSize) return;
        var font = _textBox.Font;
        var lines = _textBox.Text.Split('\n');
        var maxWidth = 40;
        foreach (var line in lines)
        {
            var w = TextRenderer.MeasureText(line + "W", font).Width;
            if (w > maxWidth) maxWidth = w;
        }

        var lineHeight = font.Height + 2;
        var totalHeight = Math.Max(1, lines.Length) * lineHeight + 4;
        _textPanel.Size = new Size(maxWidth + 2, totalHeight + 2);
    }

    private void TextPanelOnPaint(object? sender, PaintEventArgs e)
    {
        if (_textPanel is null) return;
        using var gripPen = new Pen(Color.FromArgb(170, 170, 170), 1);
        var r = _textPanel.ClientRectangle;
        e.Graphics.DrawLine(gripPen, r.Right - 9, r.Bottom - 3, r.Right - 3, r.Bottom - 9);
        e.Graphics.DrawLine(gripPen, r.Right - 13, r.Bottom - 3, r.Right - 3, r.Bottom - 13);
    }

    private static bool IsModifierOnly(Keys keyCode) =>
        keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu;

    private Point GetPointInTextPanel(object? sender, MouseEventArgs e)
    {
        if (_textPanel is null) return e.Location;
        if (sender == _textPanel) return e.Location;
        return _textPanel.PointToClient(((Control)sender!).PointToScreen(e.Location));
    }

    private bool IsTextResizeGrip(Point panelPoint)
    {
        if (_textPanel is null) return false;
        return panelPoint.X >= _textPanel.Width - 14 && panelPoint.Y >= _textPanel.Height - 14;
    }

    private bool IsTextBorderZone(Point panelPoint)
    {
        if (_textPanel is null) return false;
        const int borderHit = 6;
        return panelPoint.X <= borderHit || panelPoint.Y <= borderHit ||
               panelPoint.X >= _textPanel.Width - borderHit || panelPoint.Y >= _textPanel.Height - borderHit;
    }

    private void TextInputMouseDown(object? sender, MouseEventArgs e)
    {
        if (_textPanel is null || e.Button != MouseButtons.Left) return;
        var panelPoint = GetPointInTextPanel(sender, e);

        if (IsTextResizeGrip(panelPoint))
        {
            _textInputResizing = true;
            _textInputManualSize = true;
            _textInputMouseOriginScreen = Control.MousePosition;
            _textInputPanelSizeOrigin = _textPanel.Size;
            _textBox?.Focus();
            return;
        }

        if (IsTextBorderZone(panelPoint))
        {
            _textInputDragging = true;
            _textInputMouseOriginScreen = Control.MousePosition;
            _textInputPanelOrigin = _textPanel.Location;
            _textBox?.Focus();
        }
    }

    private void TextInputMouseMove(object? sender, MouseEventArgs e)
    {
        if (_textPanel is null) return;

        var panelPoint = GetPointInTextPanel(sender, e);
        if (_textInputResizing)
        {
            var current = Control.MousePosition;
            var dx = current.X - _textInputMouseOriginScreen.X;
            var dy = current.Y - _textInputMouseOriginScreen.Y;
            var w = Math.Max(42, _textInputPanelSizeOrigin.Width + dx);
            var h = Math.Max(_textBox?.Font.Height + 8 ?? 24, _textInputPanelSizeOrigin.Height + dy);
            _textPanel.Size = new Size(w, h);
            _textPanel.Invalidate();
            return;
        }

        if (_textInputDragging)
        {
            var current = Control.MousePosition;
            var dx = current.X - _textInputMouseOriginScreen.X;
            var dy = current.Y - _textInputMouseOriginScreen.Y;
            _textPanel.Location = new Point(_textInputPanelOrigin.X + dx, _textInputPanelOrigin.Y + dy);
            _textInputPosition = new Point(_textPanel.Left + _textPanel.Padding.Left, _textPanel.Top + _textPanel.Padding.Top);
            return;
        }

        if (IsTextResizeGrip(panelPoint))
            Cursor = Cursors.SizeNWSE;
        else if (IsTextBorderZone(panelPoint))
            Cursor = Cursors.SizeAll;
        else
            Cursor = Cursors.IBeam;
    }

    private void TextInputMouseUp(object? sender, MouseEventArgs e)
    {
        _textInputDragging = false;
        _textInputResizing = false;
    }

    private void CommitTextInput()
    {
        if (_textBox is null) return;
        var text = _textBox.Text.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // If editing an existing annotation, remove the old one first
            if (_editingAnnotationIndex >= 0 && _editingAnnotationIndex < _annotations.Count)
                _annotations.RemoveAt(_editingAnnotationIndex);

            _annotations.Add(new TextAnnotation
            {
                Position = _textInputPosition,
                Text = text,
                Color = _color,
                FontSize = _textFontSize,
                Bold = _textInputBold,
                Italic = _textInputItalic
            });
        }
        else if (_editingAnnotationIndex >= 0 && _editingAnnotationIndex < _annotations.Count)
        {
            // Deleting text = remove the annotation
            _annotations.RemoveAt(_editingAnnotationIndex);
        }

        _editingAnnotationIndex = -1;
        RemoveTextInput();
        Invalidate();
    }

    private void RemoveTextInput()
    {
        if (_textPanel is not null)
        {
            Controls.Remove(_textPanel);
            _textPanel.Dispose();
            _textPanel = null;
            _textBox = null;
            _textInputDragging = false;
            _textInputResizing = false;
            _textInputManualSize = false;
            _textInputBold = false;
            _textInputItalic = false;
        }
        else if (_textBox is not null)
        {
            Controls.Remove(_textBox);
            _textBox.Dispose();
            _textBox = null;
            _textInputDragging = false;
            _textInputResizing = false;
            _textInputManualSize = false;
            _textInputBold = false;
            _textInputItalic = false;
        }
    }

    #endregion

    #region Visual helpers (handles, shadow, magnifier)

    private void DrawSelectionHandles(Graphics g)
    {
        const int hs = 6;
        var sel = _selection;
        var pts = new[]
        {
            new Point(sel.Left, sel.Top),
            new Point(sel.Left + sel.Width / 2, sel.Top),
            new Point(sel.Right, sel.Top),
            new Point(sel.Right, sel.Top + sel.Height / 2),
            new Point(sel.Right, sel.Bottom),
            new Point(sel.Left + sel.Width / 2, sel.Bottom),
            new Point(sel.Left, sel.Bottom),
            new Point(sel.Left, sel.Top + sel.Height / 2),
        };

        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 174, 255), 1);
        foreach (var pt in pts)
        {
            var r = new Rectangle(pt.X - hs / 2, pt.Y - hs / 2, hs, hs);
            g.FillRectangle(fill, r);
            g.DrawRectangle(border, r);
        }
    }

    private void DrawToolbarShadow(Graphics g)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 12; i > 0; i -= 2)
        {
            var sr = new Rectangle(
                _toolbar.Left - i, _toolbar.Top - i + 4,
                _toolbar.Width + i * 2, _toolbar.Height + i * 2);
            var alpha = (int)(20.0 * (12 - i) / 12);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            using var path = RoundedButton.RoundedRect(sr, _toolbar.CornerRadius + i);
            g.FillPath(brush, path);
        }
        g.SmoothingMode = prev;
    }

    private void DrawMagnifier(Graphics g, Point cursor)
    {
        if (_screenBitmap is null) return;

        const int magSize = 120;
        const int zoomLevel = 4;
        var srcSize = magSize / zoomLevel;

        var srcX = Math.Clamp(cursor.X - srcSize / 2, 0, Math.Max(0, _screenBitmap.Width - srcSize));
        var srcY = Math.Clamp(cursor.Y - srcSize / 2, 0, Math.Max(0, _screenBitmap.Height - srcSize));
        var srcRect = new Rectangle(srcX, srcY, srcSize, srcSize);

        // Position below-right of cursor
        var dx = cursor.X + 20;
        var dy = cursor.Y + 20;
        if (dx + magSize + 10 > ClientSize.Width) dx = cursor.X - magSize - 20;
        if (dy + magSize + 36 > ClientSize.Height) dy = cursor.Y - magSize - 36;
        if (dx < 4) dx = 4;
        if (dy < 4) dy = 4;

        var destRect = new Rectangle(dx, dy, magSize, magSize);

        // Background card
        var cardRect = new Rectangle(dx - 4, dy - 4, magSize + 8, magSize + 32);
        using (var cardBrush = new SolidBrush(Color.FromArgb(220, 24, 24, 24)))
        using (var cardPath = RoundedButton.RoundedRect(cardRect, 6))
            g.FillPath(cardBrush, cardPath);

        // Zoomed image
        var prevInterp = g.InterpolationMode;
        var prevPixel = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_screenBitmap, destRect, srcRect, GraphicsUnit.Pixel);
        g.InterpolationMode = prevInterp;
        g.PixelOffsetMode = prevPixel;

        // Border
        using (var pen = new Pen(Color.FromArgb(0, 174, 255), 1))
            g.DrawRectangle(pen, destRect);

        // Crosshair
        var cx = dx + magSize / 2;
        var cy = dy + magSize / 2;
        using (var crossPen = new Pen(Color.FromArgb(140, 0, 174, 255), 1))
        {
            g.DrawLine(crossPen, cx, dy, cx, dy + magSize);
            g.DrawLine(crossPen, dx, cy, dx + magSize, cy);
        }

        // Coordinates text
        var text = $"({cursor.X}, {cursor.Y})";
        using var font = new Font("Consolas", 9f);
        using var textBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        g.DrawString(text, font, textBrush, dx + 4, dy + magSize + 5);
    }

    #endregion

    #region Toolbar positioning

    private void PositionToolbar()
    {
        var optionsGap = _optionsPanel.Visible ? 4 : 0;
        var optionsWidth = _optionsPanel.Visible ? _optionsPanel.Width : 0;
        var totalWidth = Math.Max(_toolbar.Width, optionsWidth);
        var totalHeight = _toolbar.Height
            + optionsGap
            + (_optionsPanel.Visible ? _optionsPanel.Height : 0);
        var selectionScreen = RectangleToScreen(_selection);
        var groupScreenSize = RectangleToScreen(
            new Rectangle(Point.Empty, new Size(totalWidth, totalHeight))).Size;
        var screen = Screen.FromPoint(new Point(
            selectionScreen.Left + selectionScreen.Width / 2,
            selectionScreen.Top + selectionScreen.Height / 2));
        var layout = CalculateToolbarLayout(
            selectionScreen,
            groupScreenSize,
            Size.Empty,
            optionsVisible: false,
            screen.WorkingArea);
        var clientLocation = PointToClient(layout.Location);
        var x = clientLocation.X;
        var y = clientLocation.Y;

        _toolbar.Location = new Point(x, y);
        _optionsPanel.Location = new Point(x, y + _toolbar.Height + optionsGap);

        // Update acrylic backdrop from screen capture
        if (_screenBitmap is not null)
        {
            _toolbar.BackdropSource = _screenBitmap;
            _toolbar.BackdropRegion = new Rectangle(x, y, _toolbar.Width, _toolbar.Height);
            _toolbar.Invalidate();
            if (_optionsPanel.Visible)
            {
                _optionsPanel.BackdropSource = _screenBitmap;
                _optionsPanel.BackdropRegion = new Rectangle(
                    _optionsPanel.Left,
                    _optionsPanel.Top,
                    _optionsPanel.Width,
                    _optionsPanel.Height);
                _optionsPanel.Invalidate();
            }
        }
    }

    internal static ToolbarLayout CalculateToolbarLayout(
        Rectangle selection,
        Size toolbarSize,
        Size optionsSize,
        bool optionsVisible,
        Size clientSize)
        => CalculateToolbarLayout(
            selection,
            toolbarSize,
            optionsSize,
            optionsVisible,
            new Rectangle(Point.Empty, clientSize));

    internal static ToolbarLayout CalculateToolbarLayout(
        Rectangle selection,
        Size toolbarSize,
        Size optionsSize,
        bool optionsVisible,
        Rectangle availableBounds)
    {
        const int screenMargin = 10;
        const int outsideGap = 10;
        const int insidePadding = 6;
        const int optionsGap = 4;

        var totalWidth = Math.Max(
            toolbarSize.Width,
            optionsVisible ? optionsSize.Width : 0);
        var totalHeight = toolbarSize.Height
            + (optionsVisible ? optionsGap + optionsSize.Height : 0);
        var minX = availableBounds.Left + screenMargin;
        var minY = availableBounds.Top + screenMargin;
        var maxX = Math.Max(minX, availableBounds.Right - totalWidth - screenMargin);
        var maxY = Math.Max(minY, availableBounds.Bottom - totalHeight - screenMargin);
        var x = Math.Clamp(selection.Left, minX, maxX);

        var belowY = selection.Bottom + outsideGap;
        if (belowY >= minY && belowY <= maxY)
        {
            return new ToolbarLayout(
                new Point(x, belowY),
                ToolbarPlacement.BelowSelection);
        }

        var aboveY = selection.Top - outsideGap - totalHeight;
        if (aboveY >= minY && aboveY <= maxY)
        {
            return new ToolbarLayout(
                new Point(x, aboveY),
                ToolbarPlacement.AboveSelection);
        }

        // There is not enough room outside the selection. Keep the complete
        // toolbar within the captured area when possible, aligned to its
        // lower edge so it remains close to the selected content.
        var insideBottomY = selection.Bottom - totalHeight - insidePadding;
        var insideTopY = selection.Top + insidePadding;
        var insideY = insideBottomY >= insideTopY
            ? insideBottomY
            : insideTopY;
        insideY = Math.Clamp(insideY, minY, maxY);
        return new ToolbarLayout(
            new Point(x, insideY),
            ToolbarPlacement.InsideSelection);
    }

    #endregion

    #region OCR

    private async Task StartOcrAsync()
    {
        if (_ocrRunning || _phase != Phase.Ready || _screenBitmap is null)
            return;

        CommitTextInput();
        _ocrMode = true;
        UpdateToolHighlight();
        UpdateCursor();
        ClearOcrResults();
        ShowOcrPanel();
        _ocrPanel!.SetLoading();
        _ocrRunning = true;
        SetOcrButtonEnabled(false);

        using var bitmap = CreateSelectedBitmap(includeAnnotations: false);
        if (bitmap is null)
        {
            _ocrPanel.SetResult(Array.Empty<OcrTextLine>(), UiText.OcrNoSelection(_settings.Language));
            _ocrRunning = false;
            SetOcrButtonEnabled(true);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _ocrCancellation = cancellation;
        try
        {
            var image = CreateOcrImage(bitmap);
            var result = await _ocrService.RecognizeAsync(image, cancellation.Token);

            if (IsDisposed || Disposing || !ReferenceEquals(_ocrCancellation, cancellation))
                return;

            _ocrLines = result.Lines;
            _ocrPanel.SetResult(result.Lines, result.ErrorMessage, result.InfoMessage);
            _selectedOcrLine = result.Lines.Count > 0 ? 0 : -1;
            PositionOcrPanel();
            Invalidate();
        }
        catch (OperationCanceledException)
        {
            // Selection changes and closing the overlay intentionally cancel OCR.
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && _ocrPanel is not null)
                _ocrPanel.SetResult(Array.Empty<OcrTextLine>(), UiText.OcrRecognitionFailed(_settings.Language, ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_ocrCancellation, cancellation))
                _ocrCancellation = null;
            _ocrRunning = false;
            if (!IsDisposed && !Disposing)
                SetOcrButtonEnabled(true);
        }
    }

    private void ShowOcrPanel()
    {
        if (_ocrPanel is not null)
            return;

        _ocrPanel = new OcrResultPanel(_settings.Language);
        _ocrPanel.LineSelected += (_, index) => SelectOcrLine(index);
        Controls.Add(_ocrPanel);
        _ocrPanel.BringToFront();
        PositionOcrPanel();
    }

    private void PositionOcrPanel()
    {
        if (_ocrPanel is null || _selection.Width < MinSelection || _selection.Height < MinSelection)
            return;

        var gap = 12;
        var x = _selection.Right + gap;
        var y = _selection.Top;

        if (x + _ocrPanel.Width > ClientSize.Width - 8)
            x = _selection.Left - _ocrPanel.Width - gap;
        if (x < 8)
            x = 8;

        if (y + _ocrPanel.Height > ClientSize.Height - 8)
            y = ClientSize.Height - _ocrPanel.Height - 8;
        if (y < 8)
            y = 8;

        _ocrPanel.Location = new Point(x, y);
        _ocrPanel.BackdropSource = _screenBitmap;
        _ocrPanel.BackdropRegion = new Rectangle(x, y, _ocrPanel.Width, _ocrPanel.Height);
        _ocrPanel.Invalidate();
        _ocrPanel.BringToFront();
    }

    private void SelectOcrLine(int index)
    {
        if (index < 0 || index >= _ocrLines.Count)
            return;

        _selectedOcrLine = index;
        _ocrPanel?.SelectLine(index);
        Invalidate();
    }

    private int HitTestOcrLine(Point pt)
    {
        for (var i = 0; i < _ocrLines.Count; i++)
        {
            var line = _ocrLines[i];
            var bounds = new Rectangle(
                _selection.Left + line.X,
                _selection.Top + line.Y,
                line.Width,
                line.Height);
            bounds.Inflate(4, 4);
            if (bounds.Contains(pt))
                return i;
        }

        return -1;
    }

    private void DrawOcrHighlights(Graphics g)
    {
        if (_ocrLines.Count == 0)
            return;

        for (var i = 0; i < _ocrLines.Count; i++)
        {
            var line = _ocrLines[i];
            var bounds = new Rectangle(
                _selection.Left + line.X,
                _selection.Top + line.Y,
                line.Width,
                line.Height);
            bounds.Inflate(2, 2);

            var selected = i == _selectedOcrLine;
            using var fill = new SolidBrush(selected
                ? Color.FromArgb(70, 255, 193, 7)
                : Color.FromArgb(45, 0, 174, 255));
            using var pen = new Pen(selected
                ? Color.FromArgb(255, 193, 7)
                : Color.FromArgb(190, 0, 174, 255), selected ? 2f : 1f);
            g.FillRectangle(fill, bounds);
            g.DrawRectangle(pen, bounds);
        }
    }

    private static OcrImage CreateOcrImage(Bitmap bitmap)
    {
        var sourceRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(sourceRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[checked(stride * bitmap.Height)];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new OcrImage(bytes, bitmap.Width, bitmap.Height, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void SetOcrButtonEnabled(bool enabled)
    {
        if (_ocrButton is null)
            return;

        _ocrButton.Enabled = enabled;
        _ocrButton.ForeColor = enabled ? Color.White : Color.FromArgb(130, 130, 130);
        _ocrButton.Invalidate();
    }

    private void ClearOcrResults()
    {
        _ocrCancellation?.Cancel();
        _ocrCancellation = null;
        _ocrRunning = false;
        _ocrLines = Array.Empty<OcrTextLine>();
        _selectedOcrLine = -1;

        if (_ocrPanel is not null)
        {
            Controls.Remove(_ocrPanel);
            _ocrPanel.Dispose();
            _ocrPanel = null;
        }

        if (!IsDisposed && !Disposing)
            SetOcrButtonEnabled(true);
        if (!IsDisposed && !Disposing)
            Invalidate();
    }

    #endregion

    #region Save / Copy / Close

    private void CopyAndClose()
    {
        CommitTextInput();
        using var bmp = CreateSelectedBitmap();
        if (bmp is not null)
            Clipboard.SetImage(bmp);
        Close();
    }

    private void PinAndClose()
    {
        CommitTextInput();
        var bmp = CreateSelectedBitmap(); // ownership transferred to PinResult → PinForm
        if (bmp is null) return;
        PinResult = new PinRequestInfo(bmp, PointToScreen(new Point(_selection.X, _selection.Y)));
        Close();
    }

    private async Task SaveAndCloseAsync()
    {
        CommitTextInput();
        using var bmp = CreateSelectedBitmap();
        if (bmp is null) return;

        var bytes = ToImageBytes(bmp, _settings.DefaultExportFormat, _settings.JpegQuality);
        await _exportManager.ExportAsync(
            _settings.SaveDirectory, _settings.FileNamePrefix,
            new ExportRequest(bytes, _settings.DefaultExportFormat),
            DateTimeOffset.Now);
        Close();
    }

    private Bitmap? CreateSelectedBitmap(bool includeAnnotations = true)
    {
        if (_screenBitmap is null || _selection.Width < 1 || _selection.Height < 1)
            return null;

        var bmp = new Bitmap(_selection.Width, _selection.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Source region from the original capture
        g.DrawImage(_screenBitmap,
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            _selection, GraphicsUnit.Pixel);

        if (includeAnnotations)
        {
            // Replay annotations (translate from form‑local to output‑bitmap coords)
            g.TranslateTransform(-_selection.X, -_selection.Y);
            foreach (var ann in _annotations)
                ann.Draw(g, _screenBitmap);
        }

        return bmp;
    }

    private static byte[] ToImageBytes(Bitmap bmp, ExportFormat fmt, int quality)
    {
        using var ms = new MemoryStream();
        if (fmt == ExportFormat.Png)
        {
            return PngEncoder.Encode(bmp);
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(static x => x.FormatID == ImageFormat.Jpeg.Guid);
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bmp.Save(ms, codec, p);
        }

        return ms.ToArray();
    }

    #endregion
}
