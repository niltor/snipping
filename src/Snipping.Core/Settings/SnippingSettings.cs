using Snipping.Core.Capture;
using Snipping.Core.Export;
using Snipping.Core.Ocr;

namespace Snipping.Core.Settings;

public sealed class SnippingSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Hotkey { get; set; } = "Ctrl+Shift+S";
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Region;
    public ExportFormat DefaultExportFormat { get; set; } = ExportFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public bool ShowEditorInTaskbar { get; set; }
    public string SaveDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snipping");
    public string FileNamePrefix { get; set; } = "snip";
    public string PinShortcut { get; set; } = "Ctrl+T";
    public int PinOpacity { get; set; } = 90;
    /// <summary>Theme preference: System | Light | Dark</summary>
    public string Theme { get; set; } = "System";
    /// <summary>UI language: zh-CN | en-US</summary>
    public string Language { get; set; } = "zh-CN";
    /// <summary>Preferred OCR language tag; empty means automatic selection.</summary>
    public string OcrPreferredLanguage { get; set; } = string.Empty;
    /// <summary>OCR backend selected by the user. Windows AI falls back to Windows OCR when unavailable.</summary>
    public OcrBackend OcrBackend { get; set; } = OcrBackend.Windows;
    public bool StartWithWindows { get; set; }
    /// <summary>Transparency percentage for pinned image window (0-90).</summary>
    public int PinWindowTransparencyPercent { get; set; } = 10;
    public bool ShowPerformanceDegradeTip { get; set; } = true;
}
