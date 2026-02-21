using System.Runtime.InteropServices;
using System.Text;
using Snipping.Core.Settings;
using Windows.System;

namespace Snipping.WinUI;

internal sealed class ShellIntegration : IDisposable
{
    private const int HotKeyId = 1001;
    private const int PinHotKeyId = 1002;

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_HOTKEY = 0x0312;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint ID_CAPTURE = 1;
    private const uint ID_PIN = 4;
    private const uint ID_SETTINGS = 2;
    private const uint ID_EXIT = 3;

    private readonly Action _captureAction;
    private readonly Action _pinAction;
    private readonly Action _openSettingsAction;
    private readonly Action _exitAction;

    private readonly WndProc _wndProc;
    private IntPtr _hWnd;
    private ushort _classAtom;
    private bool _hotKeyRegistered;

    private SnippingSettings _settings;

    public ShellIntegration(
        SnippingSettings settings,
        Action captureAction,
        Action pinAction,
        Action openSettingsAction,
        Action exitAction)
    {
        _settings = settings;
        _captureAction = captureAction;
        _pinAction = pinAction;
        _openSettingsAction = openSettingsAction;
        _exitAction = exitAction;

        _wndProc = WindowProc;
        CreateMessageWindow();
        AddTrayIcon();
        RegisterConfiguredHotkeys();
    }

    public void ApplySettings(SnippingSettings settings)
    {
        _settings = settings;
        UpdateTrayTip();
        RegisterConfiguredHotkeys();
    }

    private string T(string zhCn, string enUs)
        => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? enUs : zhCn;

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandle(null);
        var className = "SnippingWinUITrayHost_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = className
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == 0)
            throw new InvalidOperationException("Failed to register window class for tray host.");

        _hWnd = CreateWindowEx(
            0,
            className,
            "SnippingWinUITrayHost",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hWnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create tray host window.");
    }

    private void AddTrayIcon()
    {
        var data = CreateNotifyIconData();
        _ = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void UpdateTrayTip()
    {
        var data = CreateNotifyIconData();
        _ = Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateNotifyIconData()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)0x7F00), // IDI_APPLICATION
            szTip = "Snipping"
        };

        return data;
    }

    private void RegisterConfiguredHotkeys()
    {
        if (_hotKeyRegistered)
        {
            UnregisterHotKey(_hWnd, HotKeyId);
            UnregisterHotKey(_hWnd, PinHotKeyId);
            _hotKeyRegistered = false;
        }

        if (!TryParseHotkey(_settings.Hotkey, out var modifiers, out var key))
            modifiers = key = 0;

        var captureRegistered = modifiers != 0 && key != 0 && RegisterHotKey(_hWnd, HotKeyId, modifiers, key);

        var pinRegistered = false;
        if (TryParseHotkey(_settings.PinShortcut, out var pinModifiers, out var pinKey))
            pinRegistered = RegisterHotKey(_hWnd, PinHotKeyId, pinModifiers, pinKey);

        _hotKeyRegistered = captureRegistered || pinRegistered;
    }

    private bool TryParseHotkey(string hotkey, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        var keyFound = false;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0002;
                continue;
            }

            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0004;
                continue;
            }

            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0001;
                continue;
            }

            if (TryParseKey(part, out var vk))
            {
                key = vk;
                keyFound = true;
            }
        }

        return keyFound;
    }

    private static bool TryParseKey(string keyText, out uint key)
    {
        key = 0;
        if (string.IsNullOrWhiteSpace(keyText)) return false;

        var s = keyText.Trim();

        if (s.Length == 1)
        {
            var c = char.ToUpperInvariant(s[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = c;
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                key = c;
                return true;
            }
        }

        if (s.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(s[1..], out var f)
            && f is >= 1 and <= 24)
        {
            key = (uint)(0x70 + (f - 1));
            return true;
        }

        if (Enum.TryParse<VirtualKey>(s, true, out var parsed))
        {
            key = (uint)parsed;
            return true;
        }

        return s.ToLowerInvariant() switch
        {
            "plus" => SetKey(0xBB, out key),
            "minus" => SetKey(0xBD, out key),
            "left" => SetKey(0x25, out key),
            "up" => SetKey(0x26, out key),
            "right" => SetKey(0x27, out key),
            "down" => SetKey(0x28, out key),
            _ => false
        };
    }

    private static bool SetKey(uint value, out uint key)
    {
        key = value;
        return true;
    }

    private void ShowPopupMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            _ = AppendMenu(menu, MF_STRING, ID_CAPTURE, T("立即截图", "Capture now"));
            _ = AppendMenu(menu, MF_STRING, ID_PIN, T("贴图（来自剪贴板）", "Pin from clipboard"));
            _ = AppendMenu(menu, MF_STRING, ID_SETTINGS, T("设置...", "Settings..."));
            _ = AppendMenu(menu, MF_SEPARATOR, 0, null);
            _ = AppendMenu(menu, MF_STRING, ID_EXIT, T("退出", "Exit"));

            _ = SetForegroundWindow(_hWnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN, pt.X, pt.Y, _hWnd, IntPtr.Zero);

            switch (cmd)
            {
                case ID_CAPTURE:
                    SafeInvoke(_captureAction);
                    break;
                case ID_PIN:
                    SafeInvoke(_pinAction);
                    break;
                case ID_SETTINGS:
                    SafeInvoke(_openSettingsAction);
                    break;
                case ID_EXIT:
                    SafeInvoke(_exitAction);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                if ((int)wParam == HotKeyId)
                    SafeInvoke(_captureAction);
                else if ((int)wParam == PinHotKeyId)
                    SafeInvoke(_pinAction);
                return IntPtr.Zero;

            case WM_TRAYICON:
                if ((uint)lParam == WM_RBUTTONUP || (uint)lParam == WM_CONTEXTMENU)
                    ShowPopupMenu();
                else if ((uint)lParam == 0x0203) // WM_LBUTTONDBLCLK
                    _captureAction();
                return IntPtr.Zero;

            case WM_COMMAND:
                switch ((uint)wParam & 0xFFFF)
                {
                    case ID_CAPTURE: SafeInvoke(_captureAction); break;
                    case ID_PIN: SafeInvoke(_pinAction); break;
                    case ID_SETTINGS: SafeInvoke(_openSettingsAction); break;
                    case ID_EXIT: SafeInvoke(_exitAction); break;
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hotKeyRegistered)
        {
            _ = UnregisterHotKey(_hWnd, HotKeyId);
            _hotKeyRegistered = false;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1
        };
        _ = Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_hWnd != IntPtr.Zero)
        {
            DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogError("ShellIntegration.SafeInvoke", ex);
        }
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}
