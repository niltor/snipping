using System.Runtime.InteropServices;
using Snipping.Core.Export;
using Snipping.Core.Ocr;
using Snipping.Core.Settings;

namespace Snipping.App;

public sealed class SnippingApplicationContext : ApplicationContext
{
    private const int HotKeyId = 1001;
    private const int WmHotKey = 0x0312;

    private readonly SettingsManager _settingsManager = new();
    private readonly ExportManager _exportManager = new();
    private IOcrService _ocrService;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipping", "settings.ini");

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly HotKeyWindow _hotKeyWindow;
    private SnippingSettings _settings;
    private readonly FeatureEntitlements _featureEntitlements = new();
    private bool _hotkeyRegistered;
    private readonly Task _startupSynchronization;

    public SnippingApplicationContext()
    {
        _settings = _settingsManager.Load(_settingsPath);
        _ocrService = OcrServiceFactory.Create(_settings);
        _applicationIcon = AppIcon.Create();

        _notifyIcon = new NotifyIcon
        {
            Text = "Snipping",
            Icon = _applicationIcon,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => StartCapture();
        _notifyIcon.MouseUp += NotifyIconOnMouseUp;

        _hotKeyWindow = new HotKeyWindow(this);
        RegisterConfiguredHotKey();
        _startupSynchronization = SynchronizeStartupAsync();
    }

    private void NotifyIconOnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        ShowNativeTrayMenu(Cursor.Position);
    }

    private void ShowNativeTrayMenu(Point screenPt)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            const uint cmdCapture = 1;
            const uint cmdSettings = 2;
            const uint cmdExit = 3;

            _ = AppendMenu(menu, MF_STRING, cmdCapture, T("立即截图", "Capture now"));
            _ = AppendMenu(menu, MF_STRING, cmdSettings, T("设置...", "Settings..."));
            _ = AppendMenu(menu, MF_SEPARATOR, 0, null);
            _ = AppendMenu(menu, MF_STRING, cmdExit, T("退出", "Exit"));

            _ = SetForegroundWindow(_hotKeyWindow.Handle);
            var cmd = TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN,
                screenPt.X,
                screenPt.Y,
                _hotKeyWindow.Handle,
                IntPtr.Zero);

            switch (cmd)
            {
                case cmdCapture:
                    StartCapture();
                    break;
                case cmdSettings:
                    _ = OpenSettingsDialogAsync();
                    break;
                case cmdExit:
                    ExitThread();
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private async Task OpenSettingsDialogAsync()
    {
        // Temporarily disable the current hotkey while user is editing shortcut settings.
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hotKeyWindow.Handle, HotKeyId);
            _hotkeyRegistered = false;
        }

        try
        {
            await _startupSynchronization;

            // Task Manager and Windows Settings can change startup state
            // while the app is running. Use the system state as the source of
            // truth when opening the settings dialog.
            try
            {
                var currentStartup = await StartupManager.ReconcileAsync(_settings.StartWithWindows);
                _settings.StartWithWindows = currentStartup.IsEnabled;
            }
            catch
            {
                // ApplyAsync below will show a localized error if the
                // registration cannot be read or changed. Keep the stored
                // preference here so the rest of Settings remains usable.
            }

            using var form = new HotkeySettingsForm(_settings);
            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            if (!HotKeyParser.TryParse(form.Hotkey, out _, out _))
            {
                MessageBox.Show(
                    T("快捷键格式无效，请使用 Ctrl+Shift+S 这种格式。", "Invalid shortcut format. Use patterns like Ctrl+Shift+S."),
                    T("设置", "Settings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _settings.Hotkey = form.Hotkey;
            _settings.PinShortcut = string.IsNullOrWhiteSpace(form.PinShortcut) ? _settings.PinShortcut : form.PinShortcut;
            _settings.PinOpacity = form.PinOpacity;
            _settings.DefaultExportFormat = form.DefaultExportFormat;
            _settings.JpegQuality = form.JpegQuality;
            _settings.SaveDirectory = string.IsNullOrWhiteSpace(form.SaveDirectory) ? _settings.SaveDirectory : form.SaveDirectory;
            _settings.Theme = form.Theme;
            _settings.Language = form.Language;
            _settings.OcrPreferredLanguage = form.OcrPreferredLanguage;
            _settings.OcrBackend = form.OcrBackend;
            StartupRegistration startupRegistration;
            try
            {
                startupRegistration = await StartupManager.ApplyAsync(form.StartWithWindows);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    T($"开机自启设置失败：{ex.Message}", $"Could not update Windows startup: {ex.Message}"),
                    T("设置", "Settings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (form.StartWithWindows && !startupRegistration.IsEnabled)
            {
                var message = startupRegistration.State switch
                {
                    StartupRegistrationState.DisabledByUser =>
                        T("Windows 已在任务管理器中禁用了此应用的开机自启，请在那里重新启用。",
                            "Windows has disabled startup for this app in Task Manager. Re-enable it there."),
                    StartupRegistrationState.DisabledByPolicy =>
                        T("开机自启被系统策略禁用，无法启用。",
                            "Startup is disabled by system policy and cannot be enabled."),
                    _ => T("Windows 没有启用此应用的开机自启。",
                            "Windows did not enable startup for this app.")
                };
                MessageBox.Show(
                    message,
                    T("设置", "Settings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _settings.StartWithWindows = startupRegistration.IsEnabled;
            await _settingsManager.SaveAsync(_settingsPath, _settings);
            _ocrService = OcrServiceFactory.Create(_settings);
        }
        finally
        {
            RegisterConfiguredHotKey();
        }
    }

    private async Task SynchronizeStartupAsync()
    {
        try
        {
            var startup = await StartupManager.ReconcileAsync(_settings.StartWithWindows);
            if (_settings.StartWithWindows == startup.IsEnabled)
                return;

            _settings.StartWithWindows = startup.IsEnabled;
            await _settingsManager.SaveAsync(_settingsPath, _settings);
        }
        catch
        {
            // Startup registration is reconciled again when Settings opens.
            // A transient package or registry problem must not prevent the
            // tray app from starting.
        }
    }

    internal void StartCapture()
    {
        var overlay = new DesktopSnippingOverlayForm(
            _settings,
            _exportManager,
            _ocrService,
            _featureEntitlements);
        overlay.PrepareScreenCapture();
        overlay.ShowDialog();

        if (overlay.PinResult is not null)
        {
            var pin = new PinForm(overlay.PinResult.Bitmap, overlay.PinResult.ScreenLocation, _settings.PinOpacity);
            pin.Show();
        }

        overlay.Dispose();
    }

    internal void OnHotKeyPressed()
    {
        StartCapture();
    }

    protected override void ExitThreadCore()
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hotKeyWindow.Handle, HotKeyId);
            _hotkeyRegistered = false;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _hotKeyWindow.DestroyHandle();

        base.ExitThreadCore();
    }

    private void RegisterConfiguredHotKey()
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hotKeyWindow.Handle, HotKeyId);
            _hotkeyRegistered = false;
        }

        if (!HotKeyParser.TryParse(_settings.Hotkey, out var modifiers, out var key))
        {
            _settings.Hotkey = "Ctrl+Shift+S";
            modifiers = 0x0002 | 0x0004;
            key = (uint)Keys.S;
        }

        _hotkeyRegistered = RegisterHotKey(_hotKeyWindow.Handle, HotKeyId, modifiers, key);
        if (!_hotkeyRegistered)
        {
            MessageBox.Show(
                T("全局快捷键注册失败，请在设置中修改为其他组合。", "Failed to register global hotkey. Please change the shortcut in Settings."),
                "Snipping",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private string T(string zhCn, string enUs)
    {
        return _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? enUs : zhCn;
    }

    private sealed class HotKeyWindow : NativeWindow
    {
        private readonly SnippingApplicationContext _context;

        public HotKeyWindow(SnippingApplicationContext context)
        {
            _context = context;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey)
            {
                _context.OnHotKeyPressed();
                return;
            }

            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
}
