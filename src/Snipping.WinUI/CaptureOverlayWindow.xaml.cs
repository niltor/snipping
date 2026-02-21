using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;
using Snipping.Core.Export;
using Snipping.Core.Settings;

namespace Snipping.WinUI;

public sealed partial class CaptureOverlayWindow : Window
{
    private readonly TaskCompletionSource<System.Drawing.Bitmap?> _tcs = new();
    private readonly System.Drawing.Bitmap _screenSnapshot;
    private readonly SnippingSettings _settings;
    private readonly ExportManager _exportManager;
    private System.Drawing.Point? _start;
    private Windows.Foundation.Rect _selection;
    private int _virtualX;
    private int _virtualY;
    private int _virtualWidth;
    private int _virtualHeight;
    private bool _isClosed;

    // Crosshair cursor handle — loaded once on first pointer move
    private IntPtr _crossCursor = IntPtr.Zero;

    public CaptureOverlayWindow(SnippingSettings settings, ExportManager exportManager)
    {
        _settings = settings;
        _exportManager = exportManager;
        InitializeComponent();
        InitializeVirtualScreenMetrics();
        _screenSnapshot = CaptureVirtualScreen();
        LogSnapshotSample();
        ConfigureWindow();
        SetupDimOverlay();
        Closed += (_, _) =>
        {
            _isClosed = true;
            if (!_tcs.Task.IsCompleted)
                _tcs.TrySetResult(null);
            _screenSnapshot.Dispose();
        };
    }

    private void LogSnapshotSample()
    {
        try
        {
            var p = new[]
            {
                new System.Drawing.Point(Math.Max(0, _virtualWidth / 4), Math.Max(0, _virtualHeight / 4)),
                new System.Drawing.Point(Math.Max(0, _virtualWidth / 2), Math.Max(0, _virtualHeight / 2)),
                new System.Drawing.Point(Math.Max(0, (_virtualWidth * 3) / 4), Math.Max(0, (_virtualHeight * 3) / 4)),
            };
            var c1 = _screenSnapshot.GetPixel(Math.Min(_virtualWidth - 1, p[0].X), Math.Min(_virtualHeight - 1, p[0].Y));
            var c2 = _screenSnapshot.GetPixel(Math.Min(_virtualWidth - 1, p[1].X), Math.Min(_virtualHeight - 1, p[1].Y));
            var c3 = _screenSnapshot.GetPixel(Math.Min(_virtualWidth - 1, p[2].X), Math.Min(_virtualHeight - 1, p[2].Y));
            App.LogError("CaptureOverlayWindow.SnapshotSample",
                new InvalidOperationException($"sample=({c1.R},{c1.G},{c1.B})|({c2.R},{c2.G},{c2.B})|({c3.R},{c3.G},{c3.B}), size={_virtualWidth}x{_virtualHeight}, offset={_virtualX},{_virtualY}"));
        }
        catch
        {
            // best effort diagnostics only
        }
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
        win.RootGrid.Focus(FocusState.Programmatic);
        return await win._tcs.Task;
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

    private void SetupDimOverlay()
    {
        // No-op: overlay uses a simple full-screen dim rectangle
    }

    private void RootGrid_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Hide quick bar — user is starting a new selection
        QuickBar.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        var p = e.GetCurrentPoint(RootGrid).Position;
        _start = new System.Drawing.Point((int)p.X, (int)p.Y);
        _selection = new Windows.Foundation.Rect(p.X, p.Y, 0, 0);
        UpdateSelectionRect();
    }

    private void RootGrid_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Apply crosshair cursor every move message
        if (_crossCursor == IntPtr.Zero)
            _crossCursor = LoadCursor(IntPtr.Zero, 32515); // IDC_CROSS
        SetCursor(_crossCursor);

        if (_start is null) return;

        var p = e.GetCurrentPoint(RootGrid).Position;
        var x = Math.Min(_start.Value.X, (int)p.X);
        var y = Math.Min(_start.Value.Y, (int)p.Y);
        var w = Math.Abs((int)p.X - _start.Value.X);
        var h = Math.Abs((int)p.Y - _start.Value.Y);
        _selection = new Windows.Foundation.Rect(x, y, w, h);
        UpdateSelectionRect();
    }

    private void RootGrid_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_start is null) return;

        _start = null;
        if (_selection.Width < 2 || _selection.Height < 2)
        {
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        // Show the floating quick-action toolbar below the selection
        ShowQuickBar();
    }

    private void ShowQuickBar()
    {
        var barLeft = _selection.X;
        var barTop  = _selection.Y + _selection.Height + 10;

        // If too close to the bottom, show above the selection
        if (barTop + 48 > _virtualHeight)
            barTop = _selection.Y - 54;

        barLeft = Math.Max(0, Math.Min(barLeft, _virtualWidth - 220));
        barTop  = Math.Max(0, barTop);

        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(QuickBar, barLeft);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(QuickBar, barTop);
        QuickBar.Visibility = Visibility.Visible;
    }

    // ── Quick-action toolbar handlers ─────────────────────────────────────────
    private async void Qb_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var action = btn.Tag as string;

        QuickBar.Visibility = Visibility.Collapsed;

        if (action == "cancel")
        {
            // Clear selection and allow the user to redraw
            _selection = default;
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility     = Visibility.Collapsed;
            HintText.Visibility      = Visibility.Visible;
            return;
        }

        var selRect = new System.Drawing.Rectangle(
            (int)_selection.X, (int)_selection.Y,
            (int)_selection.Width, (int)_selection.Height);

        switch (action)
        {
            case "edit":
            {
                var bmp = CropFromSnapshot(selRect);
                _tcs.TrySetResult(bmp);   // caller opens full editor
                Close();
                break;
            }
            case "copy":
            {
                using var bmp = CropFromSnapshot(selRect);
                CopyBitmapToClipboard(bmp);
                _tcs.TrySetResult(null);
                Close();
                break;
            }
            case "save":
            {
                using var bmp = CropFromSnapshot(selRect);
                var bytes = BitmapToBytes(bmp);
                try
                {
                    await _exportManager.ExportAsync(
                        _settings.SaveDirectory, _settings.FileNamePrefix,
                        new ExportRequest(bytes, _settings.DefaultExportFormat, _settings.JpegQuality),
                        DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    App.LogError("CaptureOverlayWindow.Save", ex);
                }
                _tcs.TrySetResult(null);
                Close();
                break;
            }
            case "pin":
            {
                var bmp = CropFromSnapshot(selRect);
                PinnedImageWindow.Open(bmp);   // bmp ownership → PinnedImageWindow
                _tcs.TrySetResult(null);
                Close();
                break;
            }
        }
    }

    // ── Clipboard & encoding helpers ──────────────────────────────────────────
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

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _tcs.TrySetResult(null);
            Close();
        }
    }

    private void UpdateSelectionRect()
    {
        if (_selection.Width <= 0 || _selection.Height <= 0)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // Show selection border
        SelectionRect.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(SelectionRect, _selection.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(SelectionRect, _selection.Y);
        SelectionRect.Width = _selection.Width;
        SelectionRect.Height = _selection.Height;

        // Size badge: show pixel dimensions near the selection
        SizeText.Text = $"{(int)_selection.Width} × {(int)_selection.Height}";
        var badgeTop = _selection.Y + _selection.Height + 8;
        if (badgeTop + 28 > _virtualHeight) badgeTop = _selection.Y - 32;
        var badgeLeft = Math.Max(0, Math.Min(_selection.X, _virtualWidth - 130));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(SizeBadge, badgeLeft);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(SizeBadge, Math.Max(0, badgeTop));
        SizeBadge.Visibility = Visibility.Visible;
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

                App.LogError("CaptureOverlayWindow.BlackFrame",
                    new InvalidOperationException($"attempt={attempt + 1}, backend={(attempt % 2 == 0 ? "BitBlt" : "CopyFromScreen")}"));

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
        {
            App.LogError("CaptureOverlayWindow.CaptureFallback",
                new InvalidOperationException("Returning last captured frame after retries."));
            return lastFrame;
        }

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

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);
}
