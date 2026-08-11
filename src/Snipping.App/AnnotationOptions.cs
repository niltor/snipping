namespace Snipping.App;

public enum ShapeRenderMode
{
    Outline,
    Fill
}

public enum ArrowHeadMode
{
    Single,
    Double
}

public enum LineStrokeStyle
{
    Solid,
    Dashed
}

/// <summary>
/// Per-tool options used for subsequently created annotations in one capture session.
/// </summary>
internal sealed class AnnotationToolOptions
{
    public int ShapeOpacity { get; set; } = 100;
    public ShapeRenderMode ShapeMode { get; set; } = ShapeRenderMode.Outline;
    public ArrowHeadMode ArrowHead { get; set; } = ArrowHeadMode.Single;
    public LineStrokeStyle LineStyle { get; set; } = LineStrokeStyle.Solid;
    public int TextFontSize { get; set; } = 18;
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }
    public int MosaicBrushWidth { get; set; } = 20;

    public void Normalize()
    {
        ShapeOpacity = Math.Clamp(ShapeOpacity, 10, 100);
        TextFontSize = Math.Clamp(TextFontSize, 10, 100);
        MosaicBrushWidth = Math.Clamp(MosaicBrushWidth, 5, 50);
    }
}
