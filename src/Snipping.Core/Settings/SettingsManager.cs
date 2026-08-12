using System.Globalization;
using System.Text;

namespace Snipping.Core.Settings;

public sealed class SettingsManager
{
    public async Task<SnippingSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new SnippingSettings();
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            return Parse(lines);
        }
        catch (IOException)
        {
            return new SnippingSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new SnippingSettings();
        }
    }

    public SnippingSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new SnippingSettings();
        }

        try
        {
            return Parse(File.ReadAllLines(filePath));
        }
        catch (IOException)
        {
            return new SnippingSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new SnippingSettings();
        }
    }

    public async Task SaveAsync(string filePath, SnippingSettings settings, CancellationToken cancellationToken = default)
    {
        Migrate(settings);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllLinesAsync(filePath, Serialize(settings), Encoding.UTF8, cancellationToken);
    }

    private static SnippingSettings Parse(IEnumerable<string> lines)
    {
        var settings = new SnippingSettings();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            switch (key)
            {
                case "Version":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                    {
                        settings.Version = version;
                    }
                    break;
                case "Hotkey":
                    settings.Hotkey = value;
                    break;
                case "DefaultCaptureMode":
                    if (Enum.TryParse<Capture.CaptureMode>(value, true, out var captureMode)
                        && Enum.IsDefined(typeof(Capture.CaptureMode), captureMode))
                    {
                        settings.DefaultCaptureMode = captureMode;
                    }
                    break;
                case "DefaultExportFormat":
                    if (Enum.TryParse<Export.ExportFormat>(value, true, out var exportFormat)
                        && Enum.IsDefined(typeof(Export.ExportFormat), exportFormat))
                    {
                        settings.DefaultExportFormat = exportFormat;
                    }
                    break;
                case "JpegQuality":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jpegQuality))
                    {
                        settings.JpegQuality = jpegQuality;
                    }
                    break;
                case "ShowEditorInTaskbar":
                    if (bool.TryParse(value, out var showEditorInTaskbar))
                    {
                        settings.ShowEditorInTaskbar = showEditorInTaskbar;
                    }
                    break;
                case "SaveDirectory":
                    settings.SaveDirectory = value;
                    break;
                case "FileNamePrefix":
                    settings.FileNamePrefix = value;
                    break;
                case "PinShortcut":
                    settings.PinShortcut = value;
                    break;
                case "PinOpacity":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pinOpacity))
                    {
                        settings.PinOpacity = pinOpacity;
                    }
                    break;
                case "Theme":
                    settings.Theme = value;
                    break;
                case "Language":
                    settings.Language = value;
                    break;
                case "OcrPreferredLanguage":
                    settings.OcrPreferredLanguage = value;
                    break;
                case "OcrBackend":
                    if (Enum.TryParse<Ocr.OcrBackend>(value, true, out var ocrBackend)
                        && Enum.IsDefined(typeof(Ocr.OcrBackend), ocrBackend))
                    {
                        settings.OcrBackend = ocrBackend;
                    }
                    break;
                case "StartWithWindows":
                    if (bool.TryParse(value, out var startWithWindows))
                    {
                        settings.StartWithWindows = startWithWindows;
                    }
                    break;
                case "PinWindowTransparencyPercent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pinWindowTransparencyPercent))
                    {
                        settings.PinWindowTransparencyPercent = pinWindowTransparencyPercent;
                    }
                    break;
                case "ShowPerformanceDegradeTip":
                    if (bool.TryParse(value, out var showPerformanceDegradeTip))
                    {
                        settings.ShowPerformanceDegradeTip = showPerformanceDegradeTip;
                    }
                    break;
            }
        }

        Migrate(settings);
        return settings;
    }

    private static string[] Serialize(SnippingSettings settings) =>
    [
        "# Snipping settings",
        $"Version={settings.Version.ToString(CultureInfo.InvariantCulture)}",
        $"Hotkey={settings.Hotkey}",
        $"DefaultCaptureMode={settings.DefaultCaptureMode}",
        $"DefaultExportFormat={settings.DefaultExportFormat}",
        $"JpegQuality={settings.JpegQuality.ToString(CultureInfo.InvariantCulture)}",
        $"ShowEditorInTaskbar={settings.ShowEditorInTaskbar.ToString(CultureInfo.InvariantCulture)}",
        $"SaveDirectory={settings.SaveDirectory}",
        $"FileNamePrefix={settings.FileNamePrefix}",
        $"PinShortcut={settings.PinShortcut}",
        $"PinOpacity={settings.PinOpacity.ToString(CultureInfo.InvariantCulture)}",
        $"Theme={settings.Theme}",
        $"Language={settings.Language}",
        $"OcrPreferredLanguage={settings.OcrPreferredLanguage}",
        $"OcrBackend={settings.OcrBackend}",
        $"StartWithWindows={settings.StartWithWindows.ToString(CultureInfo.InvariantCulture)}",
        $"PinWindowTransparencyPercent={settings.PinWindowTransparencyPercent.ToString(CultureInfo.InvariantCulture)}",
        $"ShowPerformanceDegradeTip={settings.ShowPerformanceDegradeTip.ToString(CultureInfo.InvariantCulture)}"
    ];

    private static void Migrate(SnippingSettings settings)
    {
        if (settings.Version <= 0)
        {
            settings.Version = SnippingSettings.CurrentVersion;
        }

        settings.JpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        settings.PinOpacity = Math.Clamp(settings.PinOpacity, 1, 100);
        if (string.IsNullOrWhiteSpace(settings.Hotkey))
        {
            settings.Hotkey = "Ctrl+Shift+S";
        }

        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            settings.SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snipping");
        }

        if (string.IsNullOrWhiteSpace(settings.FileNamePrefix))
        {
            settings.FileNamePrefix = "snip";
        }

        if (string.IsNullOrWhiteSpace(settings.Theme))
        {
            settings.Theme = "System";
        }
        else if (!settings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase)
                 && !settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                 && !settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
        {
            settings.Theme = "System";
        }

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            settings.Language = "zh-CN";
        }
        else if (!settings.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                 && !settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        {
            settings.Language = "zh-CN";
        }

        settings.OcrPreferredLanguage = settings.OcrPreferredLanguage?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(typeof(Ocr.OcrBackend), settings.OcrBackend))
        {
            settings.OcrBackend = Ocr.OcrBackend.Windows;
        }
        settings.PinWindowTransparencyPercent = Math.Clamp(settings.PinWindowTransparencyPercent, 0, 90);
    }
}
