using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Snipping.Core.Export;
using Snipping.Core.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;

namespace Snipping.WinUI;

public sealed partial class CaptureOverlayWindow : Window
{
    private static readonly object ActiveOverlayLock = new();
    private static WeakReference<CaptureOverlayWindow>? _activeOverlayRef;

    private enum EditorTool { Rectangle, Ellipse, Arrow, Pen, Highlight, Mosaic, Text }

    private readonly TaskCompletionSource<System.Drawing.Bitmap?> _tcs = new();
    private readonly System.Drawing.Bitmap _screenSnapshot;
    private readonly SnippingSettings _settings;
    private readonly ExportManager _exportManager;

    private Windows.Foundation.Point? _start;
    private Windows.Foundation.Rect _selection;
    private int _virtualX;
    private int _virtualY;
    private int _virtualWidth;
    private int _virtualHeight;
    private double _rasterizationScale = 1.0;
    private bool _isClosed;
    private IntPtr _crossCursor = IntPtr.Zero;

    private bool _isInlineEditing;
    private System.Drawing.Bitmap? _inlineBaseBitmap;
    private readonly List<UIElement> _inlineCommittedElements = [];

    private Windows.Foundation.Point? _inlineStartPoint;
    private UIElement? _inlinePreviewElement;
    private Polyline? _inlinePreviewPolyline;
    private Canvas? _mosaicStrokeCanvas;

    private EditorTool _currentTool = EditorTool.Rectangle;
    private Color _currentColor = Color.FromArgb(255, 229, 69, 58);
    private double _currentThickness = 4.0;

    private ToggleButton[] _toolBtns = [];
    private Button[] _swatches = [];
    private Button? _selectedSwatch;

    private TextBlock? _selectedTextBlock;
    private TextBox? _activeTextEditor;
    private bool _isDraggingSelectedText;
    private Windows.Foundation.Point _dragStartPoint;
    private double _dragOriginX;
    private double _dragOriginY;
    private readonly Thumb[] _resizeThumbs = [];

    public CaptureOverlayWindow(SnippingSettings settings, ExportManager exportManager)
    {
        _settings = settings;
        _exportManager = exportManager;
        InitializeComponent();

        _toolBtns = [ToolRect, ToolEllipse, ToolArrow, ToolPen, ToolHighlight, ToolMosaic, ToolText];
        _swatches = [SwatchRed, SwatchYellow, SwatchBlue, SwatchGreen, SwatchWhite, SwatchBlack];
        _selectedSwatch = SwatchRed;
        _resizeThumbs = [ResizeThumbTL, ResizeThumbTC, ResizeThumbTR, ResizeThumbRC, ResizeThumbBR, ResizeThumbBC, ResizeThumbBL, ResizeThumbLC];

        SetActiveOverlay(this);

        InitializeVirtualScreenMetrics();
        _screenSnapshot = CaptureVirtualScreen();
        ConfigureWindow();

        Closed += (_, _) =>
        {
            _isClosed = true;
            SetActiveOverlay(this, clearOnly: true);
            if (!_tcs.Task.IsCompleted)
                _tcs.TrySetResult(null);
            _inlineBaseBitmap?.Dispose();
            _screenSnapshot.Dispose();
        };
    }

    public static async Task<bool> TryPinFromActiveOverlayAsync()
    {
        CaptureOverlayWindow? window = null;
        lock (ActiveOverlayLock)
        {
            if (_activeOverlayRef is not null)
                _activeOverlayRef.TryGetTarget(out window);
        }

        if (window is null || window._isClosed)
            return false;

        return await window.TryPinCurrentAsync();
    }

    public static async Task<System.Drawing.Bitmap?> CaptureAsync(SnippingSettings settings, ExportManager exportManager)
    {
        var win = new CaptureOverlayWindow(settings, exportManager);

        if (!await win.EnsureBackdropLoadedAsync())
        {
            win.Close();
            return null;
        }

        win.Activate();
        win.UpdateRasterizationScale();
        win.RootGrid.Focus(FocusState.Programmatic);
        return await win._tcs.Task;
    }

    private void UpdateRasterizationScale()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        _rasterizationScale = scale > 0 ? scale : 1.0;
    }

    private System.Drawing.Rectangle GetSelectionSourceRect()
    {
        var left = (int)Math.Floor(_selection.X * _rasterizationScale);
        var top = (int)Math.Floor(_selection.Y * _rasterizationScale);
        var right = (int)Math.Ceiling((_selection.X + _selection.Width) * _rasterizationScale);
        var bottom = (int)Math.Ceiling((_selection.Y + _selection.Height) * _rasterizationScale);

        var x = left;
        var y = top;
        var w = Math.Max(1, right - left);
        var h = Math.Max(1, bottom - top);

        x = Math.Clamp(x, 0, Math.Max(0, _screenSnapshot.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _screenSnapshot.Height - 1));
        w = Math.Clamp(w, 1, _screenSnapshot.Width - x);
        h = Math.Clamp(h, 1, _screenSnapshot.Height - y);

        return new System.Drawing.Rectangle(x, y, w, h);
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_BORDER = 0x00800000;
        const int WS_DLGFRAME = 0x00400000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_CAPTION = WS_BORDER | WS_DLGFRAME;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_POPUP = unchecked((int)0x80000000);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;

        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
        style |= WS_POPUP;
        _ = SetWindowLong(hwnd, GWL_STYLE, style);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        _ = SetWindowPos(hwnd, HWND_TOPMOST, _virtualX, _virtualY, _virtualWidth, _virtualHeight, SWP_SHOWWINDOW);
    }

    private void InitializeVirtualScreenMetrics()
    {
        _virtualX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (_virtualWidth <= 0 || _virtualHeight <= 0)
            throw new InvalidOperationException("Invalid virtual screen size.");
    }

    private void RootGrid_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isInlineEditing) return;

        HintText.Visibility = Visibility.Collapsed;
        var p = e.GetCurrentPoint(RootGrid).Position;
        _start = p;
        _selection = new Windows.Foundation.Rect(p.X, p.Y, 0, 0);
        UpdateSelectionRect();
    }

    private void RootGrid_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isInlineEditing) return;

        if (_crossCursor == IntPtr.Zero)
            _crossCursor = LoadCursor(IntPtr.Zero, 32515);
        SetCursor(_crossCursor);

        if (_start is null) return;

        var p = e.GetCurrentPoint(RootGrid).Position;
        var x = Math.Min(_start.Value.X, p.X);
        var y = Math.Min(_start.Value.Y, p.Y);
        var w = Math.Abs(p.X - _start.Value.X);
        var h = Math.Abs(p.Y - _start.Value.Y);
        _selection = new Windows.Foundation.Rect(x, y, w, h);
        UpdateSelectionRect();
    }

    private async void RootGrid_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isInlineEditing || _start is null) return;

        _start = null;
        if (_selection.Width < 2 || _selection.Height < 2)
        {
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        await EnterInlineEditorAsync();
    }

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_activeTextEditor is not null)
            return;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        if (!_isInlineEditing)
            return;

        if (IsCtrlDown())
        {
            if (e.Key == Windows.System.VirtualKey.Z)
            {
                InlineUndoButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.C)
            {
                InlineCopyButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.S)
            {
                InlineSaveButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.T)
            {
                InlinePinButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.R: SelectTool(ToolRect); e.Handled = true; break;
            case Windows.System.VirtualKey.E: SelectTool(ToolEllipse); e.Handled = true; break;
            case Windows.System.VirtualKey.A: SelectTool(ToolArrow); e.Handled = true; break;
            case Windows.System.VirtualKey.P: SelectTool(ToolPen); e.Handled = true; break;
            case Windows.System.VirtualKey.H: SelectTool(ToolHighlight); e.Handled = true; break;
            case Windows.System.VirtualKey.M: SelectTool(ToolMosaic); e.Handled = true; break;
            case Windows.System.VirtualKey.T: SelectTool(ToolText); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter: InlineDoneButton_OnClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private async Task EnterInlineEditorAsync()
    {
        _isInlineEditing = true;
        await RebuildInlineBaseFromSelectionAsync(clearAnnotations: true);
        if (_inlineBaseBitmap is not null)
            App.SetLastCapture(_inlineBaseBitmap);

        InlineEditorHost.Visibility = Visibility.Visible;
        InlineToolbar.Visibility = Visibility.Visible;
        foreach (var t in _resizeThumbs) t.Visibility = Visibility.Visible;

        SelectionRect.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;

        PlaceInlineToolbarAndHandle();
    }

    private void ExitInlineEditor(bool resetSelection)
    {
        _isInlineEditing = false;
        InlineToolbar.Visibility = Visibility.Collapsed;
        InlineEditorHost.Visibility = Visibility.Collapsed;
        foreach (var t in _resizeThumbs) t.Visibility = Visibility.Collapsed;
        InlineDrawCanvas.Children.Clear();
        _inlineCommittedElements.Clear();

        _selectedTextBlock = null;
        _activeTextEditor = null;

        if (resetSelection)
        {
            _selection = default;
            HintText.Visibility = Visibility.Visible;
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RebuildInlineBaseFromSelectionAsync(bool clearAnnotations)
    {
        _inlineBaseBitmap?.Dispose();
        _inlineBaseBitmap = CropFromSnapshot(GetSelectionSourceRect());

        if (clearAnnotations)
        {
            InlineDrawCanvas.Children.Clear();
            _inlineCommittedElements.Clear();
            _selectedTextBlock = null;
            _activeTextEditor = null;
        }

        await LoadInlineBaseImageAsync(_inlineBaseBitmap);

        InlineEditorSurface.Width = _selection.Width;
        InlineEditorSurface.Height = _selection.Height;
        InlineDrawCanvas.Width = _selection.Width;
        InlineDrawCanvas.Height = _selection.Height;

        InlineEditorSurface.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _selection.Width, _selection.Height)
        };
        InlineDrawCanvas.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _selection.Width, _selection.Height)
        };

        Canvas.SetLeft(InlineEditorHost, _selection.X);
        Canvas.SetTop(InlineEditorHost, _selection.Y);
    }

    private async Task LoadInlineBaseImageAsync(System.Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var source = new BitmapImage();
        await source.SetSourceAsync(ms.AsRandomAccessStream());
        InlineBaseImage.Source = source;
    }

    private void PlaceInlineToolbarAndHandle()
    {
        var left = _selection.X;
        var top = _selection.Y + _selection.Height + 10;
        if (top + 100 > _virtualHeight)
            top = _selection.Y - 110;

        left = Math.Max(0, Math.Min(left, _virtualWidth - 900));
        top = Math.Max(0, top);

        Canvas.SetLeft(InlineToolbar, left);
        Canvas.SetTop(InlineToolbar, top);

        var x = _selection.X;
        var y = _selection.Y;
        var w = _selection.Width;
        var h = _selection.Height;

        Canvas.SetLeft(ResizeThumbTL, x - 6); Canvas.SetTop(ResizeThumbTL, y - 6);
        Canvas.SetLeft(ResizeThumbTC, x + w / 2 - 6); Canvas.SetTop(ResizeThumbTC, y - 6);
        Canvas.SetLeft(ResizeThumbTR, x + w - 6); Canvas.SetTop(ResizeThumbTR, y - 6);
        Canvas.SetLeft(ResizeThumbRC, x + w - 6); Canvas.SetTop(ResizeThumbRC, y + h / 2 - 6);
        Canvas.SetLeft(ResizeThumbBR, x + w - 6); Canvas.SetTop(ResizeThumbBR, y + h - 6);
        Canvas.SetLeft(ResizeThumbBC, x + w / 2 - 6); Canvas.SetTop(ResizeThumbBC, y + h - 6);
        Canvas.SetLeft(ResizeThumbBL, x - 6); Canvas.SetTop(ResizeThumbBL, y + h - 6);
        Canvas.SetLeft(ResizeThumbLC, x - 6); Canvas.SetTop(ResizeThumbLC, y + h / 2 - 6);
    }

    private async void ResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isInlineEditing || sender is not Thumb t || t.Tag is not string dir) return;

        var x = _selection.X;
        var y = _selection.Y;
        var w = _selection.Width;
        var h = _selection.Height;

        if (dir.Contains('e')) w += e.HorizontalChange;
        if (dir.Contains('s')) h += e.VerticalChange;
        if (dir.Contains('w')) { x += e.HorizontalChange; w -= e.HorizontalChange; }
        if (dir.Contains('n')) { y += e.VerticalChange; h -= e.VerticalChange; }

        var minW = 80.0;
        var minH = 60.0;
        var maxRight = (double)_virtualWidth;
        var maxBottom = (double)_virtualHeight;

        w = Math.Clamp(w, minW, maxRight - x);
        h = Math.Clamp(h, minH, maxBottom - y);
        x = Math.Clamp(x, 0, maxRight - minW);
        y = Math.Clamp(y, 0, maxBottom - minH);

        _selection = new Windows.Foundation.Rect(x, y, w, h);
        await RebuildInlineBaseFromSelectionAsync(clearAnnotations: true);
        if (_inlineBaseBitmap is not null)
            App.SetLastCapture(_inlineBaseBitmap);
        PlaceInlineToolbarAndHandle();
    }

    private void ToolBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        foreach (var btn in _toolBtns)
            btn.IsChecked = btn == clicked;

        _currentTool = clicked.Name switch
        {
            nameof(ToolEllipse) => EditorTool.Ellipse,
            nameof(ToolArrow) => EditorTool.Arrow,
            nameof(ToolPen) => EditorTool.Pen,
            nameof(ToolHighlight) => EditorTool.Highlight,
            nameof(ToolMosaic) => EditorTool.Mosaic,
            nameof(ToolText) => EditorTool.Text,
            _ => EditorTool.Rectangle
        };
    }

    private void SelectTool(ToggleButton target)
    {
        foreach (var btn in _toolBtns)
            btn.IsChecked = btn == target;
        ToolBtn_Click(target, new RoutedEventArgs());
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;

        if (_selectedSwatch is not null)
            _selectedSwatch.BorderThickness = new Thickness(0);

        clicked.BorderThickness = new Thickness(2);
        _selectedSwatch = clicked;
        _currentColor = ParseColor(clicked.Tag as string ?? "#FFE5453A");

        if (_activeTextEditor is not null)
        {
            _activeTextEditor.Foreground = new SolidColorBrush(_currentColor);
            return;
        }

        if (_selectedTextBlock is not null)
            _selectedTextBlock.Foreground = new SolidColorBrush(_currentColor);
    }

    private async void InlineDrawCanvas_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInlineEditing) return;

        _selectedTextBlock = null;

        if (_activeTextEditor is not null && e.OriginalSource is DependencyObject source && IsDescendantOf(source, _activeTextEditor))
        {
            e.Handled = true;
            return;
        }

        var p = ClampPointToCanvas(e.GetCurrentPoint(InlineDrawCanvas).Position);

        if (_activeTextEditor is not null && _currentTool != EditorTool.Text)
            CommitActiveTextEditor();

        if (_currentTool == EditorTool.Text)
        {
            BeginInlineTextInput(p, null);
            e.Handled = true;
            return;
        }

        _inlineStartPoint = p;

        if (_currentTool == EditorTool.Pen || _currentTool == EditorTool.Highlight)
        {
            var strokeColor = _currentTool == EditorTool.Highlight
                ? Color.FromArgb(120, _currentColor.R, _currentColor.G, _currentColor.B)
                : _currentColor;
            var strokeThickness = _currentTool == EditorTool.Highlight
                ? Math.Max(10, _currentThickness * 3)
                : _currentThickness;

            _inlinePreviewPolyline = new Polyline
            {
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = strokeThickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _inlinePreviewPolyline.Points.Add(p);
            _inlinePreviewElement = _inlinePreviewPolyline;
            InlineDrawCanvas.Children.Add(_inlinePreviewPolyline);
        }
        else if (_currentTool == EditorTool.Mosaic)
        {
            _mosaicStrokeCanvas = new Canvas();
            _inlinePreviewElement = _mosaicStrokeCanvas;
            InlineDrawCanvas.Children.Add(_mosaicStrokeCanvas);
            AddMosaicStamp(p);
        }
        else
        {
            _inlinePreviewElement = CreatePreviewElement(_inlineStartPoint.Value, p);
            InlineDrawCanvas.Children.Add(_inlinePreviewElement);
        }

        InlineDrawCanvas.CapturePointer(e.Pointer);
    }

    private void InlineDrawCanvas_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInlineEditing || _inlineStartPoint is null || _inlinePreviewElement is null) return;

        var p = ClampPointToCanvas(e.GetCurrentPoint(InlineDrawCanvas).Position);

        if ((_currentTool == EditorTool.Pen || _currentTool == EditorTool.Highlight) && _inlinePreviewPolyline is not null)
        {
            _inlinePreviewPolyline.Points.Add(p);
            return;
        }

        if (_currentTool == EditorTool.Mosaic)
        {
            AddMosaicStamp(p);
            return;
        }

        InlineDrawCanvas.Children.Remove(_inlinePreviewElement);
        _inlinePreviewElement = CreatePreviewElement(_inlineStartPoint.Value, p);
        InlineDrawCanvas.Children.Add(_inlinePreviewElement);
    }

    private void InlineDrawCanvas_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInlineEditing || _inlineStartPoint is null || _inlinePreviewElement is null) return;

        InlineDrawCanvas.ReleasePointerCaptures();
        _inlineCommittedElements.Add(_inlinePreviewElement);
        _inlinePreviewElement = null;
        _inlinePreviewPolyline = null;
        _mosaicStrokeCanvas = null;
        _inlineStartPoint = null;
    }

    private void InlineDrawCanvas_OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCtrlDown())
            return;

        var delta = e.GetCurrentPoint(InlineDrawCanvas).Properties.MouseWheelDelta;
        var step = delta > 0 ? 1.0 : -1.0;

        if (_activeTextEditor is not null)
        {
            _activeTextEditor.FontSize = Math.Clamp(_activeTextEditor.FontSize + step, 10, 120);
            e.Handled = true;
            return;
        }

        if (_selectedTextBlock is not null)
        {
            _selectedTextBlock.FontSize = Math.Clamp(_selectedTextBlock.FontSize + step, 10, 120);
            e.Handled = true;
        }
    }

    private static bool IsCtrlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private UIElement CreatePreviewElement(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var brush = new SolidColorBrush(_currentColor);
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        if (_currentTool == EditorTool.Rectangle)
        {
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = brush,
                StrokeThickness = _currentThickness,
                Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            return rect;
        }

        if (_currentTool == EditorTool.Ellipse)
        {
            var ellipse = new Ellipse
            {
                Width = Math.Max(w, 2),
                Height = Math.Max(h, 2),
                Stroke = brush,
                StrokeThickness = _currentThickness,
                Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            return ellipse;
        }

        return CreateArrow(start, end, brush, _currentThickness);
    }

    private static UIElement CreateArrow(Windows.Foundation.Point start, Windows.Foundation.Point end, SolidColorBrush brush, double thickness)
    {
        var group = new Canvas();
        var main = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        group.Children.Add(main);

        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var arrowLen = Math.Max(10, thickness * 4);
        foreach (var wing in new[] { angle + Math.PI * 0.8, angle - Math.PI * 0.8 })
        {
            group.Children.Add(new Line
            {
                X1 = end.X,
                Y1 = end.Y,
                X2 = end.X + arrowLen * Math.Cos(wing),
                Y2 = end.Y + arrowLen * Math.Sin(wing),
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return group;
    }

    private void AddMosaicStamp(Windows.Foundation.Point point)
    {
        if (_mosaicStrokeCanvas is null || _inlineBaseBitmap is null)
            return;

        var cell = Math.Max(6, (int)(_currentThickness * 3));
        var canvasW = Math.Max(1.0, InlineDrawCanvas.Width);
        var canvasH = Math.Max(1.0, InlineDrawCanvas.Height);
        var sx = _inlineBaseBitmap.Width / canvasW;
        var sy = _inlineBaseBitmap.Height / canvasH;

        var x = Math.Clamp((int)Math.Round(point.X * sx) - cell / 2, 0, Math.Max(0, _inlineBaseBitmap.Width - cell));
        var y = Math.Clamp((int)Math.Round(point.Y * sy) - cell / 2, 0, Math.Max(0, _inlineBaseBitmap.Height - cell));

        var c = AverageColor(_inlineBaseBitmap, x, y, cell, cell);
        var canvasX = x / sx;
        var canvasY = y / sy;
        var canvasCellW = Math.Max(1.0, cell / sx);
        var canvasCellH = Math.Max(1.0, cell / sy);
        var r = new Rectangle
        {
            Width = canvasCellW,
            Height = canvasCellH,
            Fill = new SolidColorBrush(c)
        };
        Canvas.SetLeft(r, canvasX);
        Canvas.SetTop(r, canvasY);
        _mosaicStrokeCanvas.Children.Add(r);
    }

    private static Color AverageColor(System.Drawing.Bitmap bmp, int x, int y, int w, int h)
    {
        long rr = 0, gg = 0, bb = 0, count = 0;
        var step = Math.Max(1, Math.Min(w, h) / 4);
        for (var yy = y; yy < y + h && yy < bmp.Height; yy += step)
        {
            for (var xx = x; xx < x + w && xx < bmp.Width; xx += step)
            {
                var p = bmp.GetPixel(xx, yy);
                rr += p.R;
                gg += p.G;
                bb += p.B;
                count++;
            }
        }

        if (count == 0) return Color.FromArgb(255, 64, 64, 64);
        return Color.FromArgb(255, (byte)(rr / count), (byte)(gg / count), (byte)(bb / count));
    }

    private void BeginInlineTextInput(Windows.Foundation.Point point, TextBlock? editingTarget)
    {
        if (_activeTextEditor is not null)
            CommitActiveTextEditor();

        var editor = new TextBox
        {
            Text = editingTarget?.Text ?? string.Empty,
            MinWidth = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                ? "Type text"
                : "输入文字",
            FontSize = editingTarget?.FontSize ?? 24,
            Foreground = new SolidColorBrush(_currentColor),
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2)
        };

        var clampedPoint = ClampPointToCanvas(point);
        var x = editingTarget is null ? clampedPoint.X : Canvas.GetLeft(editingTarget);
        var y = editingTarget is null ? clampedPoint.Y : Canvas.GetTop(editingTarget);

        Canvas.SetLeft(editor, x);
        Canvas.SetTop(editor, y);

        if (editingTarget is not null)
        {
            InlineDrawCanvas.Children.Remove(editingTarget);
            _inlineCommittedElements.Remove(editingTarget);
            _selectedTextBlock = null;
        }

        editor.KeyDown += ActiveTextEditor_OnKeyDown;

        _activeTextEditor = editor;
        InlineDrawCanvas.Children.Add(editor);
        _inlineCommittedElements.Add(editor);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_activeTextEditor, editor))
                return;

            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        });
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ActiveTextEditor_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CommitActiveTextEditor();
            e.Handled = true;
        }
    }

    private void CommitActiveTextEditor()
    {
        if (_activeTextEditor is null) return;

        var editor = _activeTextEditor;
        _activeTextEditor = null;

        editor.KeyDown -= ActiveTextEditor_OnKeyDown;

        var text = editor.Text ?? string.Empty;
        var left = Canvas.GetLeft(editor);
        var top = Canvas.GetTop(editor);
        var size = editor.FontSize;

        InlineDrawCanvas.Children.Remove(editor);
        _inlineCommittedElements.Remove(editor);

        if (string.IsNullOrWhiteSpace(text))
            return;

        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(_currentColor),
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900
        };

        HookTextBlockInteractions(block);

        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        InlineDrawCanvas.Children.Add(block);
        _inlineCommittedElements.Add(block);
        _selectedTextBlock = null;
    }

    private void CancelActiveTextEditor()
    {
        if (_activeTextEditor is null) return;

        var editor = _activeTextEditor;
        _activeTextEditor = null;

        editor.KeyDown -= ActiveTextEditor_OnKeyDown;
        InlineDrawCanvas.Children.Remove(editor);
        _inlineCommittedElements.Remove(editor);
    }

    private void HookTextBlockInteractions(TextBlock block)
    {
        block.PointerPressed += TextBlock_OnPointerPressed;
        block.PointerMoved += TextBlock_OnPointerMoved;
        block.PointerReleased += TextBlock_OnPointerReleased;
        block.DoubleTapped += TextBlock_OnDoubleTapped;
    }

    private void TextBlock_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBlock block) return;

        _selectedTextBlock = block;
        _isDraggingSelectedText = true;
        _dragStartPoint = e.GetCurrentPoint(InlineDrawCanvas).Position;
        _dragOriginX = Canvas.GetLeft(block);
        _dragOriginY = Canvas.GetTop(block);
        block.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TextBlock_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelectedText || sender is not TextBlock block) return;

        var p = e.GetCurrentPoint(InlineDrawCanvas).Position;
        var dx = p.X - _dragStartPoint.X;
        var dy = p.Y - _dragStartPoint.Y;
        var targetX = _dragOriginX + dx;
        var targetY = _dragOriginY + dy;

        var maxX = Math.Max(0, InlineDrawCanvas.Width - block.ActualWidth);
        var maxY = Math.Max(0, InlineDrawCanvas.Height - block.ActualHeight);

        Canvas.SetLeft(block, Math.Clamp(targetX, 0, maxX));
        Canvas.SetTop(block, Math.Clamp(targetY, 0, maxY));
        e.Handled = true;
    }

    private void TextBlock_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBlock block) return;
        _isDraggingSelectedText = false;
        block.ReleasePointerCaptures();
        e.Handled = true;
    }

    private void TextBlock_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not TextBlock block) return;
        BeginInlineTextInput(new Windows.Foundation.Point(Canvas.GetLeft(block), Canvas.GetTop(block)), block);
        e.Handled = true;
    }

    private async void InlineUndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeTextEditor is not null)
        {
            CancelActiveTextEditor();
            return;
        }

        if (_inlineCommittedElements.Count == 0) return;

        var last = _inlineCommittedElements[^1];
        _inlineCommittedElements.RemoveAt(_inlineCommittedElements.Count - 1);
        InlineDrawCanvas.Children.Remove(last);
        _selectedTextBlock = ReferenceEquals(_selectedTextBlock, last) ? null : _selectedTextBlock;

        await Task.CompletedTask;
    }

    private async void InlineCopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        CommitActiveTextEditor();
        using var bmp = await RenderInlineBitmapAsync();
        CopyBitmapToClipboard(bmp);
        App.SetLastCapture(bmp);
        CompleteAndCloseCapture();
    }

    private async void InlineSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        CommitActiveTextEditor();
        using var bmp = await RenderInlineBitmapAsync();
        await SaveBitmapAsync(bmp);
        App.SetLastCapture(bmp);
        CompleteAndCloseCapture();
    }

    private async void InlinePinButton_OnClick(object sender, RoutedEventArgs e)
    {
        await TryPinCurrentAsync();
    }

    private async Task<bool> TryPinCurrentAsync()
    {
        if (!_isInlineEditing || _inlineBaseBitmap is null)
            return false;

        CommitActiveTextEditor();
        using var bmp = await RenderInlineBitmapAsync();
        App.SetLastCapture(bmp);
        PinnedImageWindow.Open(bmp, _settings.PinWindowTransparencyPercent);
        CompleteAndCloseCapture();
        return true;
    }

    private async void InlineDoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        CommitActiveTextEditor();
        using var bmp = await RenderInlineBitmapAsync();
        CopyBitmapToClipboard(bmp);
        await SaveBitmapAsync(bmp);
        App.SetLastCapture(bmp);
        CompleteAndCloseCapture();
    }

    private void CompleteAndCloseCapture()
    {
        if (!_tcs.Task.IsCompleted)
            _tcs.TrySetResult(null);

        if (!_isClosed)
            Close();
    }

    private void InlineCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExitInlineEditor(resetSelection: true);
    }

    private async Task<System.Drawing.Bitmap> RenderInlineBitmapAsync()
    {
        await Task.Delay(10);
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(InlineEditorSurface);
        var buffer = await rtb.GetPixelsAsync();
        var bytes = buffer.ToArray();

        var bmp = new System.Drawing.Bitmap(rtb.PixelWidth, rtb.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, rtb.PixelWidth, rtb.PixelHeight),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private async Task SaveBitmapAsync(System.Drawing.Bitmap bmp)
    {
        var bytes = BitmapToBytes(bmp);
        await _exportManager.ExportAsync(
            _settings.SaveDirectory,
            _settings.FileNamePrefix,
            new ExportRequest(bytes, _settings.DefaultExportFormat, _settings.JpegQuality),
            DateTimeOffset.Now);
    }

    private static void CopyBitmapToClipboard(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms.AsRandomAccessStream()));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private byte[] BitmapToBytes(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        if (_settings.DefaultExportFormat == ExportFormat.Png)
        {
            bmp.Save(ms, ImageFormat.Png);
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            var param = new EncoderParameters(1);
            param.Param[0] = new EncoderParameter(Encoder.Quality, (long)_settings.JpegQuality);
            bmp.Save(ms, codec, param);
        }
        return ms.ToArray();
    }

    private void UpdateSelectionRect()
    {
        if (_selection.Width <= 0 || _selection.Height <= 0)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _selection.X);
        Canvas.SetTop(SelectionRect, _selection.Y);
        SelectionRect.Width = _selection.Width;
        SelectionRect.Height = _selection.Height;

        SizeText.Text = $"{(int)_selection.Width} × {(int)_selection.Height}";
        var badgeTop = _selection.Y + _selection.Height + 8;
        if (badgeTop + 28 > _virtualHeight) badgeTop = _selection.Y - 32;
        var badgeLeft = Math.Max(0, Math.Min(_selection.X, _virtualWidth - 130));
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, Math.Max(0, badgeTop));
        SizeBadge.Visibility = Visibility.Visible;
    }

    private System.Drawing.Bitmap CropFromSnapshot(System.Drawing.Rectangle rect)
    {
        var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.DrawImage(
            _screenSnapshot,
            new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height),
            rect,
            System.Drawing.GraphicsUnit.Pixel);
        return bmp;
    }

    private System.Drawing.Bitmap CaptureVirtualScreen()
    {
        Exception? lastError = null;
        System.Drawing.Bitmap? lastFrame = null;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var bmp = attempt % 2 == 0
                    ? CaptureVirtualScreenWithBitBlt()
                    : CaptureVirtualScreenWithCopyFromScreen();

                if (!IsLikelyBlackFrame(bmp))
                {
                    lastFrame?.Dispose();
                    return bmp;
                }

                lastFrame?.Dispose();
                lastFrame = bmp;
            }
            catch (Exception ex)
            {
                lastError = ex;
                App.LogError("CaptureOverlayWindow.CaptureAttempt", ex);
            }

            Thread.Sleep(40);
        }

        if (lastFrame is not null)
            return lastFrame;

        throw new InvalidOperationException("Failed to capture any screen frame.", lastError);
    }

    private System.Drawing.Bitmap CaptureVirtualScreenWithBitBlt()
    {
        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            throw new InvalidOperationException("GetDC failed for desktop.");

        var hdcMem = CreateCompatibleDC(hdcScreen);
        if (hdcMem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
            throw new InvalidOperationException("CreateCompatibleDC failed.");
        }

        var hBitmap = CreateCompatibleBitmap(hdcScreen, _virtualWidth, _virtualHeight);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            throw new InvalidOperationException("CreateCompatibleBitmap failed.");
        }

        var hOld = SelectObject(hdcMem, hBitmap);
        try
        {
            const int SRCCOPY = 0x00CC0020;
            const int CAPTUREBLT = 0x40000000;
            if (!BitBlt(hdcMem, 0, 0, _virtualWidth, _virtualHeight, hdcScreen, _virtualX, _virtualY, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed.");

            using var raw = System.Drawing.Image.FromHbitmap(hBitmap);
            var bmp = new System.Drawing.Bitmap(_virtualWidth, _virtualHeight, PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.DrawImageUnscaled(raw, 0, 0);
            return bmp;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private System.Drawing.Bitmap CaptureVirtualScreenWithCopyFromScreen()
    {
        var bmp = new System.Drawing.Bitmap(_virtualWidth, _virtualHeight, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(
            new System.Drawing.Point(_virtualX, _virtualY),
            System.Drawing.Point.Empty,
            new System.Drawing.Size(_virtualWidth, _virtualHeight));
        return bmp;
    }

    private static bool IsLikelyBlackFrame(System.Drawing.Bitmap bmp)
    {
        const int grid = 5;
        var stepX = Math.Max(1, bmp.Width / (grid + 1));
        var stepY = Math.Max(1, bmp.Height / (grid + 1));

        var nonBlack = 0;
        for (var gx = 1; gx <= grid; gx++)
        {
            for (var gy = 1; gy <= grid; gy++)
            {
                var x = Math.Min(bmp.Width - 1, gx * stepX);
                var y = Math.Min(bmp.Height - 1, gy * stepY);
                var c = bmp.GetPixel(x, y);
                if (c.R > 2 || c.G > 2 || c.B > 2)
                    nonBlack++;
            }
        }

        return nonBlack == 0;
    }

    private async Task<bool> EnsureBackdropLoadedAsync()
    {
        if (_isClosed || BackdropImage.Source is not null)
            return true;

        try
        {
            using var ms = new MemoryStream();
            _screenSnapshot.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var source = new BitmapImage();
            await source.SetSourceAsync(ms.AsRandomAccessStream());

            if (_isClosed)
                return false;

            BackdropImage.Source = source;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
            return Color.FromArgb(
                Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[4..6], 16), Convert.ToByte(s[6..8], 16));
        if (s.Length == 6)
            return Color.FromArgb(255,
                Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[4..6], 16));
        return Color.FromArgb(255, 229, 69, 58);
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private Windows.Foundation.Point ClampPointToCanvas(Windows.Foundation.Point p)
    {
        var width = Math.Max(1.0, InlineDrawCanvas.Width);
        var height = Math.Max(1.0, InlineDrawCanvas.Height);
        return new Windows.Foundation.Point(
            Math.Clamp(p.X, 0, width),
            Math.Clamp(p.Y, 0, height));
    }

    private static void SetActiveOverlay(CaptureOverlayWindow? window, bool clearOnly = false)
    {
        lock (ActiveOverlayLock)
        {
            if (clearOnly)
            {
                if (window is null || _activeOverlayRef is null)
                    return;

                if (_activeOverlayRef.TryGetTarget(out var existing) && !ReferenceEquals(existing, window))
                    return;

                _activeOverlayRef = null;
                return;
            }

            if (window is null)
            {
                _activeOverlayRef = null;
                return;
            }

            _activeOverlayRef = new WeakReference<CaptureOverlayWindow>(window);
        }
    }
}
