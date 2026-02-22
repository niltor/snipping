using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace Snipping.WinUI;

public sealed partial class PinnedImageWindow : Window
{
    private static readonly List<PinnedImageWindow> OpenWindows = [];
    private readonly System.Drawing.Bitmap _bitmap;
    private readonly int _transparencyPercent;
    private AppWindow? _appWindow;
    private bool _isDraggingWindow;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartWindowX;
    private int _dragStartWindowY;

    public PinnedImageWindow(System.Drawing.Bitmap bitmap, int transparencyPercent)
    {
        InitializeComponent();
        _bitmap = (System.Drawing.Bitmap)bitmap.Clone();
        _transparencyPercent = Math.Clamp(transparencyPercent, 0, 90);

        Closed += (_, _) =>
        {
            _bitmap.Dispose();
            OpenWindows.Remove(this);
        };

        ConfigureWindow();
        _ = LoadImageAsync();
    }

    public static void Open(System.Drawing.Bitmap bitmap, int transparencyPercent = 10)
    {
        var win = new PinnedImageWindow(bitmap, transparencyPercent);
        OpenWindows.Add(win);
        win.Activate();
        win.RootGrid.Focus(FocusState.Programmatic);
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow = appWindow;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable   = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Start with the original image size.
        var w = Math.Max(1, _bitmap.Width);
        var h = Math.Max(1, _bitmap.Height);
        appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        // Centre on primary display
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        appWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width  - w) / 2,
            work.Y + (work.Height - h) / 2));

        // Hide from taskbar (WS_EX_TOOLWINDOW) and keep always-on-top
        const int GWL_EXSTYLE     = -20;
        const int GWL_STYLE       = -16;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_BORDER = 0x00800000;
        const int WS_DLGFRAME = 0x00400000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_CAPTION = WS_BORDER | WS_DLGFRAME;
        const int WS_POPUP = unchecked((int)0x80000000);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;

        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
        style |= WS_POPUP;
        SetWindowLong(hwnd, GWL_STYLE, style);

        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_LAYERED);

        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        // Remove possible system-drawn border tint on borderless windows (Win11+).
        const uint DWMWA_BORDER_COLOR = 34;
        unchecked
        {
            var none = (int)0xFFFFFFFE; // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
        }

        const uint LWA_ALPHA = 0x2;
        var alpha = (byte)Math.Clamp((int)Math.Round(255 * (1.0 - _transparencyPercent / 100.0)), 0, 255);
        _ = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);

        PinImage.Opacity = 1.0;
    }

    private async Task LoadImageAsync()
    {
        using var ms = new MemoryStream();
        _bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var source = new BitmapImage();
        await source.SetSourceAsync(ms.AsRandomAccessStream());
        PinImage.Source = source;
    }

    // Drag the window by pressing anywhere on the image
    private void RootGrid_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed is false)
            return;

        if (_appWindow is null)
            return;

        if (!GetCursorPos(out var pt))
            return;

        var start = _appWindow.Position;
        _isDraggingWindow = true;
        _dragStartCursorX = pt.X;
        _dragStartCursorY = pt.Y;
        _dragStartWindowX = start.X;
        _dragStartWindowY = start.Y;

        RootGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void RootGrid_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow || _appWindow is null)
            return;

        if (!GetCursorPos(out var pt))
            return;

        var dx = pt.X - _dragStartCursorX;
        var dy = pt.Y - _dragStartCursorY;

        _appWindow.Move(new Windows.Graphics.PointInt32(
            _dragStartWindowX + dx,
            _dragStartWindowY + dy));

        e.Handled = true;
    }

    private void RootGrid_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow)
            return;

        _isDraggingWindow = false;
        RootGrid.ReleasePointerCaptures();
        e.Handled = true;
    }

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
            Close();
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("dwmapi.dll")] private static extern int  DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}

