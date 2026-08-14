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

public sealed partial class CaptureEditorWindow : Window
{
    private enum EditorTool { Rectangle, Ellipse, Arrow, Pen, Text }

    // ── State fields ──────────────────────────────────────────────────────────
    private EditorTool _currentTool = EditorTool.Rectangle;
    private Color _currentColor = Color.FromArgb(255, 229, 69, 58); // red
    private double _currentThickness = 4.0;

    // ── Control groups for mutual exclusion ───────────────────────────────────
    private ToggleButton[] _toolBtns = [];
    private Button[] _swatches = [];
    private ToggleButton[] _thickBtns = [];
    private Button? _selectedSwatch;

    // ── Drawing state ─────────────────────────────────────────────────────────
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly ExportManager _exportManager;
    private readonly SnippingSettings _settings;
    private readonly System.Drawing.Bitmap _baseBitmap;
    private readonly List<UIElement> _committedElements = [];

    private Windows.Foundation.Point? _startPoint;
    private UIElement? _previewElement;
    private Polyline? _previewPolyline;

    private bool IsEnglish => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    // ── Construction ──────────────────────────────────────────────────────────
    public CaptureEditorWindow(System.Drawing.Bitmap bitmap, SnippingSettings settings, ExportManager exportManager)
    {
        InitializeComponent();
        _settings = settings;
        _exportManager = exportManager;
        _baseBitmap = (System.Drawing.Bitmap)bitmap.Clone();

        // Collect control groups for mutual-exclusion management
        _toolBtns = [ToolRect, ToolEllipse, ToolArrow, ToolPen, ToolText];
        _swatches = [SwatchRed, SwatchOrange, SwatchYellow, SwatchGreen, SwatchBlue, SwatchPurple, SwatchWhite, SwatchBlack];
        _thickBtns = [ThickThin, ThickMed, ThickBold];
        _selectedSwatch = SwatchRed;

        Closed += (_, _) =>
        {
            if (!_tcs.Task.IsCompleted) _tcs.TrySetResult(false);
            _baseBitmap.Dispose();
        };

        Localize();
        ConfigureWindow();
        _ = LoadBaseImageAsync();
    }

    public static async Task OpenAsync(System.Drawing.Bitmap bitmap, SnippingSettings settings, ExportManager exportManager)
    {
        var window = new CaptureEditorWindow(bitmap, settings, exportManager);
        window.Activate();
        window.RootGrid.Focus(FocusState.Programmatic);
        await window._tcs.Task;
    }

    // ── Window setup ──────────────────────────────────────────────────────────
    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Size the window to the image plus some padding, capped to available work area
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;

        const int toolbarH = 56;
        const int padding = 80;
        var w = Math.Min(_baseBitmap.Width + 48, work.Width - padding);
        var h = Math.Min(_baseBitmap.Height + toolbarH + 60, work.Height - padding);
        w = Math.Max(w, 700);
        h = Math.Max(h, 500);

        appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        appWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - w) / 2,
            work.Y + (work.Height - h) / 2));

        appWindow.Title = $"Snipping Editor — {_baseBitmap.Width} \u00d7 {_baseBitmap.Height}";
    }

    private async Task LoadBaseImageAsync()
    {
        using var ms = new MemoryStream();
        _baseBitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var source = new BitmapImage();
        await source.SetSourceAsync(ms.AsRandomAccessStream());
        BaseImage.Source = source;
        EditorSurface.Width = _baseBitmap.Width;
        EditorSurface.Height = _baseBitmap.Height;
        OverlayCanvas.Width = _baseBitmap.Width;
        OverlayCanvas.Height = _baseBitmap.Height;
    }

    // ── Toolbar event handlers ────────────────────────────────────────────────
    private void ToolBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        // Uncheck all others
        foreach (var btn in _toolBtns) btn.IsChecked = btn == clicked;
        _currentTool = clicked.Name switch
        {
            nameof(ToolEllipse) => EditorTool.Ellipse,
            nameof(ToolArrow)   => EditorTool.Arrow,
            nameof(ToolPen)     => EditorTool.Pen,
            nameof(ToolText)    => EditorTool.Text,
            _                   => EditorTool.Rectangle
        };
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;
        SelectSwatch(clicked);
    }

    private void SelectSwatch(Button clicked)
    {
        // Remove selection ring from previous swatch
        if (_selectedSwatch is not null)
            _selectedSwatch.BorderThickness = new Thickness(0);

        clicked.BorderThickness = new Thickness(2);
        _selectedSwatch = clicked;
        _currentColor = ParseColor(clicked.Tag as string ?? "#FFE5453A");
    }

    private void ThickBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var btn in _thickBtns) btn.IsChecked = btn == clicked;
        _currentThickness = double.TryParse(clicked.Tag as string, out var v) ? v : 4.0;
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                _tcs.TrySetResult(false);
                Close();
                break;
            // Tool shortcuts — only when no modifier held
            case Windows.System.VirtualKey.R when !IsModifierDown():
                ActivateTool(ToolRect); break;
            case Windows.System.VirtualKey.E when !IsModifierDown():
                ActivateTool(ToolEllipse); break;
            case Windows.System.VirtualKey.A when !IsModifierDown():
                ActivateTool(ToolArrow); break;
            case Windows.System.VirtualKey.P when !IsModifierDown():
                ActivateTool(ToolPen); break;
            case Windows.System.VirtualKey.T when !IsModifierDown():
                ActivateTool(ToolText); break;
            default:
            {
                if (!IsModifierDown())
                {
                    var colorIndex = GetColorShortcutIndex(e.Key);
                    if (colorIndex >= 0 && colorIndex < _swatches.Length)
                    {
                        SelectSwatch(_swatches[colorIndex]);
                        e.Handled = true;
                    }
                }

                break;
            }
        }
    }

    private static int GetColorShortcutIndex(Windows.System.VirtualKey key)
    {
        if (key >= Windows.System.VirtualKey.Number1 && key <= Windows.System.VirtualKey.Number5)
            return (int)(key - Windows.System.VirtualKey.Number1);

        if (key >= Windows.System.VirtualKey.NumberPad1 && key <= Windows.System.VirtualKey.NumberPad5)
            return (int)(key - Windows.System.VirtualKey.NumberPad1);

        return -1;
    }

    private static bool IsModifierDown()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void ActivateTool(ToggleButton target)
    {
        foreach (var btn in _toolBtns) btn.IsChecked = btn == target;
        ToolBtn_Click(target, new RoutedEventArgs());
    }

    // ── Pointer / drawing ─────────────────────────────────────────────────────
    private async void OverlayCanvas_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(OverlayCanvas).Position;

        if (_currentTool == EditorTool.Text)
        {
            await InsertTextAsync(p);
            return;
        }

        _startPoint = p;

        if (_currentTool == EditorTool.Pen)
        {
            _previewPolyline = new Polyline
            {
                Stroke = new SolidColorBrush(_currentColor),
                StrokeThickness = _currentThickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _previewPolyline.Points.Add(p);
            _previewElement = _previewPolyline;
            OverlayCanvas.Children.Add(_previewPolyline);
        }
        else
        {
            _previewElement = CreatePreviewElement(_startPoint.Value, p);
            OverlayCanvas.Children.Add(_previewElement);
        }

        OverlayCanvas.CapturePointer(e.Pointer);
    }

    private void OverlayCanvas_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_startPoint is null || _previewElement is null) return;
        var p = e.GetCurrentPoint(OverlayCanvas).Position;

        if (_currentTool == EditorTool.Pen && _previewPolyline is not null)
        {
            _previewPolyline.Points.Add(p);
            return;
        }

        OverlayCanvas.Children.Remove(_previewElement);
        _previewElement = CreatePreviewElement(_startPoint.Value, p);
        OverlayCanvas.Children.Add(_previewElement);
    }

    private void OverlayCanvas_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_startPoint is null || _previewElement is null) return;
        OverlayCanvas.ReleasePointerCaptures();
        _committedElements.Add(_previewElement);
        _previewElement = null;
        _previewPolyline = null;
        _startPoint = null;
    }

    private UIElement CreatePreviewElement(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var brush = new SolidColorBrush(_currentColor);
        var fillBrush = new SolidColorBrush(Color.FromArgb(16, _currentColor.R, _currentColor.G, _currentColor.B));
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        if (_currentTool == EditorTool.Rectangle)
        {
            var rect = new Rectangle
            {
                Width = w, Height = h,
                Stroke = brush, StrokeThickness = _currentThickness,
                Fill = fillBrush
            };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            return rect;
        }

        if (_currentTool == EditorTool.Ellipse)
        {
            var ellipse = new Ellipse
            {
                Width = Math.Max(w, 2), Height = Math.Max(h, 2),
                Stroke = brush, StrokeThickness = _currentThickness,
                Fill = fillBrush
            };
            Canvas.SetLeft(ellipse, x); Canvas.SetTop(ellipse, y);
            return ellipse;
        }

        return CreateArrow(start, end, brush, _currentThickness);
    }

    private static UIElement CreateArrow(Windows.Foundation.Point start, Windows.Foundation.Point end, SolidColorBrush brush, double thickness)
    {
        var group = new Canvas();
        var main = new Line
        {
            X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
            Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        group.Children.Add(main);

        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var arrowLen = Math.Max(10, thickness * 4);
        foreach (var wing in new[] { angle + Math.PI * 0.8, angle - Math.PI * 0.8 })
        {
            group.Children.Add(new Line
            {
                X1 = end.X, Y1 = end.Y,
                X2 = end.X + arrowLen * Math.Cos(wing),
                Y2 = end.Y + arrowLen * Math.Sin(wing),
                Stroke = brush, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
        return group;
    }

    private async Task InsertTextAsync(Windows.Foundation.Point point)
    {
        var input = new TextBox
        {
            AcceptsReturn = false,
            MinWidth = 280,
            PlaceholderText = IsEnglish ? "Type text here" : "输入文字"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = IsEnglish ? "Add text" : "添加文字",
            Content = input,
            PrimaryButtonText = IsEnglish ? "Add" : "添加",
            SecondaryButtonText = IsEnglish ? "Cancel" : "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(_currentColor),
            FontSize = Math.Max(14, _currentThickness * 5),
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxWidth = 600
        };
        Canvas.SetLeft(tb, point.X); Canvas.SetTop(tb, point.Y);
        OverlayCanvas.Children.Add(tb);
        _committedElements.Add(tb);
    }

    // ── Action handlers ───────────────────────────────────────────────────────
    private void UndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_committedElements.Count == 0) return;
        var last = _committedElements[^1];
        _committedElements.RemoveAt(_committedElements.Count - 1);
        OverlayCanvas.Children.Remove(last);
    }

    private async void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var bmp = await RenderCurrentBitmapAsync();
        CopyToClipboard(bmp);
        _ = ShowToastAsync(IsEnglish ? "Copied to clipboard" : "已复制到剪贴板");
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var bmp = await RenderCurrentBitmapAsync();
            var bytes = ToImageBytes(bmp, _settings.DefaultExportFormat, _settings.JpegQuality);
            var path = await _exportManager.ExportAsync(
                _settings.SaveDirectory, _settings.FileNamePrefix,
                new ExportRequest(bytes, _settings.DefaultExportFormat, _settings.JpegQuality),
                DateTimeOffset.Now);
            _ = ShowToastAsync(IsEnglish ? $"Saved to {path}" : $"已保存到 {path}");
        }
        catch (Exception ex)
        {
            _ = ShowToastAsync(IsEnglish ? $"Save failed: {ex.Message}" : $"保存失败：{ex.Message}", isError: true);
        }
    }

    private async void DoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var bmp = await RenderCurrentBitmapAsync();
        CopyToClipboard(bmp);
        var bytes = ToImageBytes(bmp, _settings.DefaultExportFormat, _settings.JpegQuality);
        await _exportManager.ExportAsync(
            _settings.SaveDirectory, _settings.FileNamePrefix,
            new ExportRequest(bytes, _settings.DefaultExportFormat, _settings.JpegQuality),
            DateTimeOffset.Now);
        _tcs.TrySetResult(true);
        Close();
    }

    private async void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var bmp = await RenderCurrentBitmapAsync();
        PinnedImageWindow.Open(bmp);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────
    private async Task<System.Drawing.Bitmap> RenderCurrentBitmapAsync()
    {
        await Task.Delay(10); // let any in-flight draw complete
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(EditorSurface);
        var buffer = await rtb.GetPixelsAsync();
        var bytes = buffer.ToArray();
        var bmp = new System.Drawing.Bitmap(rtb.PixelWidth, rtb.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, rtb.PixelWidth, rtb.PixelHeight),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static void CopyToClipboard(System.Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms.AsRandomAccessStream()));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static byte[] ToImageBytes(System.Drawing.Bitmap bmp, ExportFormat fmt, int quality)
    {
        using var ms = new MemoryStream();
        if (fmt == ExportFormat.Png)
        {
            bmp.Save(ms, ImageFormat.Png);
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            var param = new EncoderParameters(1);
            param.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bmp.Save(ms, codec, param);
        }
        return ms.ToArray();
    }

    // ── Toast notification ────────────────────────────────────────────────────
    private async Task ShowToastAsync(string message, bool isError = false)
    {
        ToastBarText.Text = message;
        ToastBarText.Foreground = isError
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        ToastBar.Visibility = Visibility.Visible;
        await Task.Delay(2800);
        ToastBar.Visibility = Visibility.Collapsed;
    }

    // ── Localize ──────────────────────────────────────────────────────────────
    private void Localize()
    {
        if (!IsEnglish) return; // XAML defaults are Chinese; only flip if English

        Title = "Snipping Editor";
        CopyLabel.Text = "Copy";
        SaveLabel.Text = "Save";
        PinLabel.Text = "Pin";
        DoneLabel.Text = "Done";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
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
        return Color.FromArgb(255, 229, 69, 58); // fallback red
    }
}
