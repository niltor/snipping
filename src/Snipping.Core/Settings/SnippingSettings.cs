using Snipping.Core.Capture;
using Snipping.Core.Export;

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
    public bool ShowPerformanceDegradeTip { get; set; } = true;
}
