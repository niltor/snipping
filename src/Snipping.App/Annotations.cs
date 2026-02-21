using System.Drawing.Drawing2D;

namespace Snipping.App;

public enum AnnotationTool
{
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Text,
    Highlight,
    Mosaic,
    FreeDraw
}

/// <summary>
/// Base class for all annotation items. Coordinates are in bitmap/local space.
/// </summary>
public abstract class AnnotationItem
{
    public abstract void Draw(Graphics g, Bitmap? sourceBitmap);
}

public sealed class RectangleAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float Thickness { get; init; } = 3;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        var rect = AnnotationHelper.Normalize(Start, End);
        if (rect.Width < 1 || rect.Height < 1) return;
        using var pen = new Pen(Color, Thickness);
        g.DrawRectangle(pen, rect);
    }
}

public sealed class EllipseAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float Thickness { get; init; } = 3;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        var rect = AnnotationHelper.Normalize(Start, End);
        if (rect.Width < 1 || rect.Height < 1) return;
        using var pen = new Pen(Color, Thickness);
        g.DrawEllipse(pen, rect);
    }
}

public sealed class ArrowAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float Thickness { get; init; } = 3;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        var dx = End.X - Start.X;
        var dy = End.Y - Start.Y;
        if (dx * dx + dy * dy < 9) return;
        using var pen = new Pen(Color, Thickness);
        pen.CustomEndCap = new AdjustableArrowCap(Thickness + 2, Thickness + 2);
        g.DrawLine(pen, Start, End);
    }
}

public sealed class LineAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float Thickness { get; init; } = 3;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        if (Start == End) return;
        using var pen = new Pen(Color, Thickness);
        g.DrawLine(pen, Start, End);
    }
}

public sealed class TextAnnotation : AnnotationItem
{
    public required Point Position { get; init; }
    public required string Text { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float FontSize { get; init; } = 18;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        if (string.IsNullOrEmpty(Text)) return;
        using var font = new Font("Microsoft YaHei UI", FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString(Text, font, brush, Position);
    }

    public Rectangle GetBounds()
    {
        using var font = new Font("Microsoft YaHei UI", FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        var size = TextRenderer.MeasureText(Text, font);
        return new Rectangle(Position.X, Position.Y, size.Width, size.Height);
    }
}

public sealed class HighlightAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        var rect = AnnotationHelper.Normalize(Start, End);
        if (rect.Width < 1 || rect.Height < 1) return;
        using var brush = new SolidBrush(Color.FromArgb(100, Color.Yellow));
        g.FillRectangle(brush, rect);
    }
}

public sealed class MosaicAnnotation : AnnotationItem
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public int BlockSize { get; init; } = 10;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        if (sourceBitmap is null) return;
        var rect = AnnotationHelper.Normalize(Start, End);
        var safeRect = Rectangle.Intersect(rect, new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height));
        if (safeRect.Width < 2 || safeRect.Height < 2) return;

        var w = Math.Max(1, safeRect.Width / BlockSize);
        var h = Math.Max(1, safeRect.Height / BlockSize);
        using var small = new Bitmap(w, h);
        using (var sg = Graphics.FromImage(small))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.DrawImage(sourceBitmap, new Rectangle(0, 0, w, h), safeRect, GraphicsUnit.Pixel);
        }

        var prevInterp = g.InterpolationMode;
        var prevOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(small, safeRect);
        g.InterpolationMode = prevInterp;
        g.PixelOffsetMode = prevOffset;
    }
}

public sealed class FreeDrawAnnotation : AnnotationItem
{
    public required List<Point> Points { get; init; }
    public Color Color { get; init; } = Color.Red;
    public float Thickness { get; init; } = 3;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        if (Points.Count < 2) return;
        using var pen = new Pen(Color, Thickness)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLines(pen, Points.ToArray());
    }
}

public sealed class MosaicBrushAnnotation : AnnotationItem
{
    public required List<Point> Points { get; init; }
    public int BrushWidth { get; init; } = 20;
    public int BlockSize { get; init; } = 10;

    public override void Draw(Graphics g, Bitmap? sourceBitmap)
    {
        if (sourceBitmap is null || Points.Count < 2) return;

        using var strokePen = new Pen(Color.Black, BrushWidth)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var path = new GraphicsPath();
        path.AddLines(Points.ToArray());

        try { path.Widen(strokePen); }
        catch { return; }

        var bounds = Rectangle.Ceiling(path.GetBounds());
        var safeRect = Rectangle.Intersect(bounds, new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height));
        if (safeRect.Width < 2 || safeRect.Height < 2) return;

        var w = Math.Max(1, safeRect.Width / BlockSize);
        var h = Math.Max(1, safeRect.Height / BlockSize);
        using var small = new Bitmap(w, h);
        using (var sg = Graphics.FromImage(small))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.DrawImage(sourceBitmap, new Rectangle(0, 0, w, h), safeRect, GraphicsUnit.Pixel);
        }

        var savedState = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(small, safeRect);
        g.Restore(savedState);
    }
}

internal static class AnnotationHelper
{
    public static Rectangle Normalize(Point a, Point b)
    {
        return new Rectangle(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
