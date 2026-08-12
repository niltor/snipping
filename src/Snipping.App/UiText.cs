using Snipping.Core.Ocr;

namespace Snipping.App;

/// <summary>
/// Centralized UI text for the two languages currently supported by the app.
/// Keeping tooltips and accessibility names here prevents the overlay from
/// silently falling back to Chinese when English is selected in settings.
/// </summary>
internal static class UiText
{
    public static string T(string? language, string zhCn, string enUs) =>
        string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ? enUs : zhCn;

    public static string AnnotationToolTip(string? language, AnnotationTool tool) =>
        tool switch
        {
            AnnotationTool.Rectangle => T(language, "矩形", "Rectangle") + " (Q)",
            AnnotationTool.Ellipse => T(language, "椭圆", "Ellipse") + " (W)",
            AnnotationTool.Arrow => T(language, "箭头", "Arrow") + " (E)",
            AnnotationTool.Line => T(language, "直线", "Line") + " (R)",
            AnnotationTool.Text => T(language, "文字", "Text") + " (T)",
            AnnotationTool.Highlight => T(language, "高亮", "Highlight") + " (A)",
            AnnotationTool.Mosaic => T(language, "马赛克", "Mosaic") + " (S)",
            AnnotationTool.FreeDraw => T(language, "画笔", "Free draw") + " (D)",
            _ => string.Empty
        };

    public static string AnnotationToolName(string? language, AnnotationTool tool) =>
        tool switch
        {
            AnnotationTool.Rectangle => T(language, "矩形", "Rectangle"),
            AnnotationTool.Ellipse => T(language, "椭圆", "Ellipse"),
            AnnotationTool.Arrow => T(language, "箭头", "Arrow"),
            AnnotationTool.Line => T(language, "直线", "Line"),
            AnnotationTool.Text => T(language, "文字", "Text"),
            AnnotationTool.Highlight => T(language, "高亮", "Highlight"),
            AnnotationTool.Mosaic => T(language, "马赛克", "Mosaic"),
            AnnotationTool.FreeDraw => T(language, "画笔", "Free draw"),
            _ => string.Empty
        };

    public static string ColorToolTip(string? language, Color color, int shortcut)
    {
        var name = color.ToArgb() switch
        {
            var value when value == Color.FromArgb(235, 64, 52).ToArgb() => ("红色", "Red"),
            var value when value == Color.FromArgb(0, 122, 255).ToArgb() => ("蓝色", "Blue"),
            var value when value == Color.FromArgb(52, 199, 89).ToArgb() => ("绿色", "Green"),
            var value when value == Color.FromArgb(255, 214, 10).ToArgb() => ("黄色", "Yellow"),
            var value when value == Color.White.ToArgb() => ("白色", "White"),
            _ => ("颜色", "Color")
        };
        return $"{T(language, name.Item1, name.Item2)} ({shortcut})";
    }

    public static string UndoToolTip(string? language) => T(language, "撤销", "Undo") + " (Ctrl+Z)";
    public static string OcrToolTip(string? language) => T(language, "识别文字", "Recognize text") + " (Ctrl+A)";
    public static string PinToolTip(string? language, string shortcut) =>
        $"{T(language, "置顶贴图", "Pin image")} ({shortcut})";
    public static string SaveToolTip(string? language) => T(language, "保存", "Save") + " (Ctrl+S)";
    public static string CopyToolTip(string? language) => T(language, "复制", "Copy") + " (Enter)";
    public static string CloseToolTip(string? language) => T(language, "关闭", "Close") + " (Esc)";

    public static string OcrResultTitle(string? language) => T(language, "OCR 识别结果", "OCR result");
    public static string CopyText(string? language) => T(language, "复制文字", "Copy text");
    public static string Recognizing(string? language) => T(language, "正在识别…", "Recognizing…");
    public static string NoTextRecognized(string? language) => T(language, "未识别到文字", "No text recognized");
    public static string RecognizedLines(string? language, int count) =>
        T(language, $"识别到 {count} 行文字", $"Recognized {count} line(s)");
    public static string TextCopied(string? language) => T(language, "文字已复制", "Text copied");
    public static string CopyFailed(string? language, string details) =>
        T(language, $"复制失败：{details}", $"Copy failed: {details}");

    public static string OcrNoSelection(string? language) =>
        T(language, "当前没有有效截图选区。", "There is no valid screenshot selection.");
    public static string OcrRecognitionFailed(string? language, string details) =>
        T(language, $"OCR 识别失败：{details}", $"OCR recognition failed: {details}");
    public static string OcrLanguagePackMissing(string? language) =>
        T(language,
            "未找到可用的 Windows OCR 语言包，请在系统设置中安装中文或英文 OCR 语言包。",
            "No Windows OCR language pack is available. Install a Chinese or English OCR language pack in Windows Settings.");
    public static string OcrInitializationFailed(string? language, string details) =>
        T(language, $"Windows OCR 初始化失败：{details}", $"Windows OCR initialization failed: {details}");
    public static string OcrEngineFailure(string? language, string tag, string details) =>
        T(language, $"{tag}: {details}", $"{tag}: {details}");
    public static string OcrPreferredUnavailable(string? language, string tag) =>
        T(language, $"设置的 OCR 语言不可用：{tag}，已按自动规则选择。", $"The configured OCR language is unavailable: {tag}. Automatic selection was used.");
    public static string OcrUsingConfiguredLanguage(string? language, string tag) =>
        T(language, $"已按设置使用：{tag}。", $"Using the configured OCR language: {tag}.");
    public static string OcrUsingConfiguredLanguageWithoutEnglish(string? language, string tag) =>
        T(language, $"已按设置使用：{tag}（未找到英文 OCR 语言包）。", $"Using the configured OCR language: {tag} (no English OCR language pack found).");
    public static string OcrUsingConfiguredMixedLanguages(string? language, string primary, string english) =>
        T(language, $"已按设置使用：{primary} + {english} 混合识别。", $"Using the configured languages: {primary} + {english} for mixed recognition.");
    public static string OcrUsingMixedLanguages(string? language, string primary, string english) =>
        T(language, $"已使用：{primary} + {english} 混合识别。", $"Using {primary} + {english} for mixed recognition.");
    public static string OcrMultipleEngines(string? language, string selected) =>
        T(language, $"检测到多个 OCR 引擎，当前使用：{selected}。", $"Multiple OCR engines found; using {selected}.");
    public static string OcrImageProcessing(string? language, OcrImagePreparation preparation)
    {
        if (preparation.WasTiled)
        {
            return T(
                language,
                preparation.WasUpscaled
                    ? $"已将小图放大并分块识别（{preparation.Slices.Count} 块）。"
                    : $"截图较大，已分块识别（{preparation.Slices.Count} 块）。",
                preparation.WasUpscaled
                    ? $"The small image was enlarged and recognized in {preparation.Slices.Count} tiles."
                    : $"The image was recognized in {preparation.Slices.Count} tiles because it is large.");
        }

        return preparation.WasUpscaled
            ? T(language, "已将小图放大后识别。", "The small image was enlarged before recognition.")
            : string.Empty;
    }

    public static string OcrWindowsAiUnavailable(string? language, string details) =>
        T(language, $"Windows AI OCR 当前不可用：{details}", $"Windows AI OCR is unavailable: {details}");

    public static string OcrWindowsAiPreparing(string? language) =>
        T(language, "正在准备 Windows AI OCR 模型…", "Preparing the Windows AI OCR model…");

    public static string OcrWindowsAiUsing(string? language) =>
        T(language, "已使用 Windows AI OCR。", "Using Windows AI OCR.");
}
