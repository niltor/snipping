using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Snipping.Core.Settings;
using WinRT.Interop;

namespace Snipping.WinUI;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly SettingsManager _settingsManager = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Snipping",
        "settings.json");
    private ShellIntegration? _shell;

    public App()
    {
        // Application.RequestedTheme MUST be set before InitializeComponent().
        // Load settings synchronously here so we can apply the initial theme.
        try
        {
            var settings = _settingsManager.Load(_settingsPath);
            var normalized = (settings.Theme ?? "System").Trim().ToLowerInvariant();
            RequestedTheme = normalized switch
            {
                "dark"  => ApplicationTheme.Dark,
                "light" => ApplicationTheme.Light,
                _       => ApplicationTheme.Light   // default neutral
            };
        }
        catch
        {
            // If settings can't be read, proceed with the default theme.
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            LogError("UnhandledException", e.Exception);
            e.Handled = true;
        }
        catch
        {
            // Last-chance handler; never throw from here.
        }
    }

    internal static void LogError(string area, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipping");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "winui-errors.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {area}: {ex}\n\n");
        }
        catch
        {
            // ignore logging failures
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = _settingsManager.Load(_settingsPath);

        // WinUI3 unpackaged apps exit when no window is alive.
        // We activate the settings window once (required), then immediately hide it
        // via Win32 so it is invisible but still keeps the app process running.
        _window = new MainWindow();
        _window.SettingsSaved += WindowOnSettingsSaved;
        _window.Activate();

        var hwnd = WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_HIDE);  // hide without closing — app stays alive

        _shell = new ShellIntegration(
            settings,
            captureAction:      () => _window.DispatcherQueue.TryEnqueue(() => _ = RunCaptureSafeAsync()),
            pinAction:          () => _window.DispatcherQueue.TryEnqueue(() => _ = RunPinSafeAsync()),
            openSettingsAction: () => _window.DispatcherQueue.TryEnqueue(() => _window.Activate()),
            exitAction:         () => _window.DispatcherQueue.TryEnqueue(() => ShutdownApp()));

        // Apply ElementTheme + DWM title bar (runtime-safe; does NOT touch Application.RequestedTheme)
        ApplyTheme(settings.Theme);
    }

    private async Task RunCaptureSafeAsync()
    {
        if (_window is null)
            return;

        try
        {
            await _window.ShowCapturePlaceholderAsync();
        }
        catch (Exception ex)
        {
            await _window.ShowRuntimeErrorAsync(ex, "Capture failed", "截图失败");
        }
    }

    private async Task RunPinSafeAsync()
    {
        if (_window is null)
            return;

        try
        {
            _window.Activate();
            await _window.PinClipboardImageAsync();
        }
        catch (Exception ex)
        {
            await _window.ShowRuntimeErrorAsync(ex, "Pin failed", "贴图失败");
        }
    }

    private void WindowOnSettingsSaved(object? sender, SnippingSettings settings)
    {
        ApplyTheme(settings.Theme);
        _shell?.ApplySettings(settings);
    }

    // Called at startup and whenever settings are saved.
    // NOTE: Application.RequestedTheme can NOT be changed at runtime (only in App() ctor).
    // For runtime theme switching, we use ElementTheme on the window content + DWM title bar.
    public void ApplyTheme(string? theme)
    {
        var normalized = (theme ?? "System").Trim().ToLowerInvariant();
        var elementTheme = normalized switch
        {
            "dark"  => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _       => ElementTheme.Default
        };

        // Update client-area XAML theme at runtime
        if (_window?.Content is FrameworkElement root)
            root.RequestedTheme = elementTheme;

        // Update non-client title bar chrome via DWM
        if (_window is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var isDark = normalized == "dark" ||
                (normalized != "light" && IsSystemInDarkMode());
            ApplyDwmDarkMode(hwnd, isDark);
        }
    }

    // ── DWM helpers ───────────────────────────────────────────────────────────
    private static bool IsSystemInDarkMode()
    {
        try
        {
            var bg = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R < 128; // dark background → dark mode
        }
        catch { return false; }
    }

    private static void ApplyDwmDarkMode(IntPtr hwnd, bool isDark)
    {
        int value = isDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
        DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void ShutdownApp()
    {
        _shell?.Dispose();
        _shell = null;
        _window?.Close();
    }
}
