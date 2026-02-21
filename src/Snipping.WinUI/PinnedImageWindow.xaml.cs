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

    public PinnedImageWindow(System.Drawing.Bitmap bitmap)
    {
        InitializeComponent();
        _bitmap = (System.Drawing.Bitmap)bitmap.Clone();

        Closed += (_, _) =>
        {
            _bitmap.Dispose();
            OpenWindows.Remove(this);
        };

        ConfigureWindow();
        _ = LoadImageAsync();
    }

    public static void Open(System.Drawing.Bitmap bitmap)
    {
        var win = new PinnedImageWindow(bitmap);
        OpenWindows.Add(win);
        win.Activate();
        win.RootGrid.Focus(FocusState.Programmatic);
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable   = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Size the window while maintaining the image aspect ratio (max 800×600)
        const int maxW = 800, maxH = 600;
        var scale = Math.Min(1.0,
            Math.Min((double)maxW / Math.Max(1, _bitmap.Width),
                     (double)maxH / Math.Max(1, _bitmap.Height)));
        var w = Math.Max(80, (int)(_bitmap.Width  * scale));
        var h = Math.Max(80, (int)(_bitmap.Height * scale));
        appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        // Centre on primary display
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        appWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width  - w) / 2,
            work.Y + (work.Height - h) / 2));

        // Hide from taskbar (WS_EX_TOOLWINDOW) and keep always-on-top
        const int GWL_EXSTYLE     = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // Ensure DWM renders a drop shadow for the floating appearance
        int policy = 2; // DWMNCRP_ENABLED
        DwmSetWindowAttribute(hwnd, 2 /* DWMWA_NCRENDERING_POLICY */, ref policy, sizeof(int));
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
        var hwnd = WindowNative.GetWindowHandle(this);
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN + HTCAPTION
    }

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
            Close();
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("dwmapi.dll")] private static extern int  DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int value, int size);
}

