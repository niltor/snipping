using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Snipping.Core.Export;
using Snipping.Core.Settings;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Snipping.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SettingsManager _settingsManager = new();
    private readonly ExportManager _exportManager = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Snipping",
        "settings.json");

    private SnippingSettings _settings = new();
    private bool _captureInProgress;
    private bool _isLoaded;

    public event EventHandler<SnippingSettings>? SettingsSaved;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ApplyLanguage();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ConfigureWindowSize();
    }

    private void ConfigureWindowSize()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        const int w = 550, h = 400;
        appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        appWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width  - w) / 2,
            work.Y + (work.Height - h) / 2));
    }

    private void LoadSettings()
    {
        _settings = _settingsManager.Load(_settingsPath);
        HotkeyTextBox.Text = _settings.Hotkey;
        PinShortcutTextBox.Text = _settings.PinShortcut;
        SaveDirectoryTextBox.Text = _settings.SaveDirectory;

        ThemeSystemRadio.IsChecked = _settings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase);
        ThemeLightRadio.IsChecked = _settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
        ThemeDarkRadio.IsChecked = _settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);

        foreach (var item in LanguageCombo.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string)?.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase) == true)
            {
                LanguageCombo.SelectedItem = item;
                break;
            }
        }
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.Hotkey = string.IsNullOrWhiteSpace(HotkeyTextBox.Text) ? _settings.Hotkey : HotkeyTextBox.Text.Trim();
        _settings.PinShortcut = string.IsNullOrWhiteSpace(PinShortcutTextBox.Text) ? _settings.PinShortcut : PinShortcutTextBox.Text.Trim();
        _settings.SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectoryTextBox.Text) ? _settings.SaveDirectory : SaveDirectoryTextBox.Text.Trim();
        _settings.Theme = ThemeDarkRadio.IsChecked == true
            ? "Dark"
            : ThemeLightRadio.IsChecked == true
                ? "Light"
                : "System";
        _settings.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";

        await _settingsManager.SaveAsync(_settingsPath, _settings);

        SettingsSaved?.Invoke(this, _settings);

        if (Application.Current is App app)
            app.ApplyTheme(_settings.Theme);
        ApplyLanguage();

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "Saved" : "已保存",
            Content = _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                ? "Settings have been saved."
                : "设置已保存。",
            CloseButtonText = _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "OK" : "确定"
        };
        _ = await dialog.ShowAsync();
    }

    public async Task ShowCapturePlaceholderAsync()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;

        try
        {
            using var bmp = await CaptureOverlayWindow.CaptureAsync(_settings, _exportManager);
            if (bmp is null) return;

            await CaptureEditorWindow.OpenAsync(bmp, _settings, _exportManager);
        }
        catch (Exception ex)
        {
            App.LogError("MainWindow.ShowCapturePlaceholderAsync", ex);
            await ShowRuntimeErrorAsync(ex, "Capture failed", "截图失败");
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    public async Task ShowRuntimeErrorAsync(Exception ex, string enTitle, string zhTitle)
    {
        var title = IsEnglish ? enTitle : zhTitle;
        var content = IsEnglish
            ? $"{ex.Message}\n\nPlease try again or change shortcut settings."
            : $"{ex.Message}\n\n请重试，或在设置中修改快捷键后再试。";

        if (Content?.XamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = IsEnglish ? "OK" : "确定"
        };
        _ = await dialog.ShowAsync();
    }

    private static void CopyToClipboard(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms.AsRandomAccessStream()));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static byte[] ToImageBytes(Bitmap bmp, ExportFormat fmt, int quality)
    {
        using var ms = new MemoryStream();
        if (fmt == ExportFormat.Png)
        {
            bmp.Save(ms, ImageFormat.Png);
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bmp.Save(ms, codec, p);
        }

        return ms.ToArray();
    }

    public async Task PinClipboardImageAsync()
    {
        var package = Clipboard.GetContent();
        if (!package.Contains(StandardDataFormats.Bitmap))
            return;

        var streamRef = await package.GetBitmapAsync();
        using var stream = await streamRef.OpenReadAsync();
        using var netStream = stream.AsStreamForRead();
        using var ms = new MemoryStream();
        await netStream.CopyToAsync(ms);
        ms.Position = 0;

        using var bmp = new Bitmap(ms);
        PinnedImageWindow.Open((Bitmap)bmp);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ThemeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (ThemeDarkRadio is null || ThemeLightRadio is null)
            return;

        var theme = ThemeDarkRadio.IsChecked == true
            ? "Dark"
            : ThemeLightRadio.IsChecked == true
                ? "Light"
                : "System";

        if (Application.Current is App app)
            app.ApplyTheme(theme);
    }

    private void LanguageCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
            return;

        _settings.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
        ApplyLanguage();
    }

    private bool IsEnglish => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    private void ApplyLanguage()
    {
        if (SaveButton is null)
            return;

        if (IsEnglish)
        {
            Title = "Snipping Settings";
            TitleText.Text = "Snipping Settings";
            HotkeyLabel.Text = "Global snip shortcut";
            PinShortcutLabel.Text = "Pin shortcut";
            SaveDirectoryLabel.Text = "Save directory";
            ThemeLabel.Text = "Theme";
            LanguageLabel.Text = "Language";
            HotkeyHintText.Text = "Click the box and press shortcut keys";
            ThemeSystemRadio.Content = "System";
            ThemeLightRadio.Content = "Light";
            ThemeDarkRadio.Content = "Dark";
            BrowseButton.Content = "Browse";
            SaveButton.Content = "Save";
            CancelButton.Content = "Cancel";
        }
        else
        {
            Title = "截图设置";
            TitleText.Text = "截图设置";
            HotkeyLabel.Text = "全局截图快捷键";
            PinShortcutLabel.Text = "置顶贴图快捷键";
            SaveDirectoryLabel.Text = "保存目录";
            ThemeLabel.Text = "主题";
            LanguageLabel.Text = "语言";
            HotkeyHintText.Text = "点击输入框后直接按组合键";
            ThemeSystemRadio.Content = "跟随系统";
            ThemeLightRadio.Content = "浅色";
            ThemeDarkRadio.Content = "深色";
            BrowseButton.Content = "浏览";
            SaveButton.Content = "保存";
            CancelButton.Content = "取消";
        }
    }

    private async void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            SaveDirectoryTextBox.Text = folder.Path;
    }

    private void ShortcutBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.Key == VirtualKey.Tab)
            return;

        if (e.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            box.Text = string.Empty;
            e.Handled = true;
            return;
        }

        var shortcut = BuildShortcutText(e.Key);
        if (!string.IsNullOrEmpty(shortcut))
            box.Text = shortcut;

        e.Handled = true;
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static string? BuildShortcutText(VirtualKey key)
    {
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu)
            return null;

        var parts = new List<string>();
        if (IsKeyDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsKeyDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsKeyDown(VirtualKey.Menu)) parts.Add("Alt");
        parts.Add(NormalizeKeyName(key));
        return string.Join("+", parts);
    }

    private static string NormalizeKeyName(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return ((int)(key - VirtualKey.Number0)).ToString();

        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return "Num" + ((int)(key - VirtualKey.NumberPad0));

        return key switch
        {
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            _ => key.ToString()
        };
    }
}
