using System.Drawing.Drawing2D;

namespace Snipping.App;

/// <summary>
/// Draws tool icons using GDI+ for crisp, scalable rendering.
/// All methods draw into a given content rectangle with a specified color.
/// </summary>
internal static class ToolIcons
{
    public static void Rectangle(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.6f);
        g.DrawRectangle(pen, box);
    }

    public static void Ellipse(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.6f);
        g.DrawEllipse(pen, box);
    }

    public static void Arrow(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        var start = new PointF(box.Left, box.Bottom);
        var end = new PointF(box.Right, box.Top);
        using var pen = new Pen(c, 1.6f);
        pen.CustomEndCap = new AdjustableArrowCap(4, 4);
        g.DrawLine(pen, start, end);
    }

    public static void Line(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.6f);
        g.DrawLine(pen, box.Left, box.Bottom, box.Right, box.Top);
    }

    public static void Text(Graphics g, Rectangle r, Color c)
    {
        // Keep the glyph visually comparable to the line-based tools.  The
        // previous 50% font size left the actual letter height noticeably
        // smaller because of the font's ascender/descender metrics.
        var side = Math.Min(r.Width, r.Height);
        // Increase the cap height while using the regular face so the glyph
        // becomes larger without becoming visually heavier.
        using var font = new Font("Segoe UI", side * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(c);
        var bounds = new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString("T", font, brush, bounds, sf);
    }

    public static void Highlight(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        var stripe = new Rectangle(box.X, box.Y + box.Height / 2 - 3, box.Width, 6);
        using (var hb = new SolidBrush(Color.FromArgb(140, Color.Yellow)))
            g.FillRectangle(hb, stripe);
        using var pen = new Pen(c, 1.2f);
        g.DrawLine(pen, box.Left + 2, box.Top + 3, box.Right - 2, box.Top + 3);
        g.DrawLine(pen, box.Left + 4, box.Bottom - 3, box.Right - 4, box.Bottom - 3);
    }

    public static void Mosaic(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        var cellW = box.Width / 3f;
        var cellH = box.Height / 3f;
        using var penGrid = new Pen(Color.FromArgb(80, c), 1f);
        using var brushDark = new SolidBrush(Color.FromArgb(100, c));
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
        {
            var cr = new RectangleF(box.X + col * cellW, box.Y + row * cellH, cellW, cellH);
            if ((row + col) % 2 == 0)
                g.FillRectangle(brushDark, cr);
            g.DrawRectangle(penGrid, cr.X, cr.Y, cr.Width, cr.Height);
        }
    }

    public static void FreeDraw(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var pts = new PointF[]
        {
            new(box.Left, cy + 2),
            new(box.Left + box.Width * 0.25f, cy - 4),
            new(cx, cy + 4),
            new(box.Left + box.Width * 0.75f, cy - 4),
            new(box.Right, cy + 2)
        };
        g.DrawCurve(pen, pts, 0.5f);
    }

    public static void Undo(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 6);
        using var pen = new Pen(c, 1.5f);
        pen.CustomStartCap = new AdjustableArrowCap(3.2f, 3.2f);
        var arc = new RectangleF(box.X, box.Y, box.Width, box.Height);
        g.DrawArc(pen, arc, 200, 230);
    }

    public static void Ocr(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.5f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Four scan corners with a small letter A and baseline.
        const int corner = 5;
        g.DrawLine(pen, box.Left, box.Top + corner, box.Left, box.Top);
        g.DrawLine(pen, box.Left, box.Top, box.Left + corner, box.Top);
        g.DrawLine(pen, box.Right - corner, box.Top, box.Right, box.Top);
        g.DrawLine(pen, box.Right, box.Top, box.Right, box.Top + corner);
        g.DrawLine(pen, box.Left, box.Bottom - corner, box.Left, box.Bottom);
        g.DrawLine(pen, box.Left, box.Bottom, box.Left + corner, box.Bottom);
        g.DrawLine(pen, box.Right - corner, box.Bottom, box.Right, box.Bottom);
        g.DrawLine(pen, box.Right, box.Bottom, box.Right, box.Bottom - corner);

        var midX = box.X + box.Width / 2f;
        var topY = box.Top + 6;
        var bottomY = box.Bottom - 6;
        g.DrawLine(pen, midX - 4, bottomY, midX, topY);
        g.DrawLine(pen, midX, topY, midX + 4, bottomY);
        g.DrawLine(pen, midX - 2.5f, box.Y + box.Height / 2f + 1, midX + 2.5f, box.Y + box.Height / 2f + 1);
    }

    public static void Save(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // A classic floppy-disk silhouette reads as "save" without suggesting
        // download/export.  The clipped top-right corner and two labels make
        // the meaning remain clear even at this small size.
        var disk = new Rectangle(box.X + 1, box.Y, box.Width - 2, box.Height);
        using var path = new GraphicsPath();
        path.AddLine(disk.Left, disk.Top, disk.Right - 6, disk.Top);
        path.AddLine(disk.Right - 6, disk.Top, disk.Right, disk.Top + 6);
        path.AddLine(disk.Right, disk.Top + 6, disk.Right, disk.Bottom);
        path.AddLine(disk.Right, disk.Bottom, disk.Left, disk.Bottom);
        path.CloseFigure();
        g.DrawPath(pen, path);

        // Disk label and write-protect/notch detail.
        g.DrawRectangle(pen, disk.Left + 4, disk.Top + 2, disk.Width - 10, 6);
        g.DrawLine(pen, disk.Right - 6, disk.Top, disk.Right - 6, disk.Top + 6);
        g.DrawRectangle(pen, disk.Left + 4, disk.Bottom - 8, disk.Width - 8, 5);
    }

    public static void Copy(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 5);
        using var pen = new Pen(c, 1.4f);
        var back = new Rectangle(box.X + 4, box.Y, box.Width - 4, box.Height - 4);
        g.DrawRectangle(pen, back);
        var front = new Rectangle(box.X, box.Y + 4, box.Width - 4, box.Height - 4);
        using (var bg = new SolidBrush(Color.FromArgb(200, 40, 40, 40)))
            g.FillRectangle(bg, front);
        g.DrawRectangle(pen, front);
    }

    public static void Pin(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 6);
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(c);

        var cx = box.X + box.Width / 2f;
        // Pin head
        g.FillEllipse(brush, cx - 3, box.Top, 6, 6);
        // Pin body (two angled lines)
        g.DrawLine(pen, cx - 5, box.Top + 8, cx + 5, box.Top + 8);
        g.DrawLine(pen, cx - 3, box.Top + 6, cx - 5, box.Top + 8);
        g.DrawLine(pen, cx + 3, box.Top + 6, cx + 5, box.Top + 8);
        // Needle
        g.DrawLine(pen, cx, box.Top + 8, cx, box.Bottom);
    }

    public static void Close(Graphics g, Rectangle r, Color c)
    {
        var box = Deflate(r, 6);
        using var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, box.Left, box.Top, box.Right, box.Bottom);
        g.DrawLine(pen, box.Right, box.Top, box.Left, box.Bottom);
    }

    private static Rectangle Deflate(Rectangle r, int d) =>
        new(r.X + d, r.Y + d, r.Width - d * 2, r.Height - d * 2);
}
