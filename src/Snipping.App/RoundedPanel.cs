using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Snipping.App;

/// <summary>
/// A rounded-corner panel that simulates acrylic/frosted glass when a <see cref="BackdropSource"/>
/// bitmap and source region are provided.
/// </summary>
public sealed class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.FromArgb(70, 70, 70);
    public int BorderThickness { get; set; } = 1;
    public Color TintColor { get; set; } = Color.FromArgb(190, 30, 30, 30);

    /// <summary>Full screen bitmap used as the blur source.</summary>
    public Bitmap? BackdropSource { get; set; }
    /// <summary>Region (in bitmap coords) that is behind this panel.</summary>
    public Rectangle BackdropRegion { get; set; }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* suppress */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = GetRoundedRectPath(rect, CornerRadius);

        // Clip to rounded rect
        g.SetClip(path);

        // Acrylic backdrop (blurred screenshot behind the panel)
        if (BackdropSource is not null && BackdropRegion.Width > 0 && BackdropRegion.Height > 0)
        {
            DrawBlurredBackdrop(g, rect);
        }

        // Tint overlay
        using (var brush = new SolidBrush(TintColor))
            g.FillRectangle(brush, rect);

        g.ResetClip();

        // Border
        if (BorderThickness > 0)
        {
            using var pen = new Pen(BorderColor, BorderThickness);
            g.DrawPath(pen, path);
        }
    }

    private void DrawBlurredBackdrop(Graphics g, Rectangle dest)
    {
        if (BackdropSource is null) return;

        var src = Rectangle.Intersect(BackdropRegion,
            new Rectangle(0, 0, BackdropSource.Width, BackdropSource.Height));
        if (src.Width < 1 || src.Height < 1) return;

        // Downsample → cheap blur
        var smallW = Math.Max(1, src.Width / 12);
        var smallH = Math.Max(1, src.Height / 12);

        using var small = new Bitmap(smallW, smallH, PixelFormat.Format32bppArgb);
        using (var sg = Graphics.FromImage(small))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.DrawImage(BackdropSource, new Rectangle(0, 0, smallW, smallH), src, GraphicsUnit.Pixel);
        }

        // Upscale with bicubic = smooth blur
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(small, dest);
    }

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
