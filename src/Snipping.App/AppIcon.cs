using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Snipping.App;

/// <summary>
/// Creates the small application/tray mark in code so the app does not need
/// a separate bitmap set. The shape is intentionally simple at tray scale.
/// </summary>
internal static class AppIcon
{
    private const int Size = 32;

    public static Icon Create()
    {
        using var bitmap = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var card = new SolidBrush(Color.FromArgb(242, 242, 242));
            using var cardPath = RoundedRect(new Rectangle(4, 4, 24, 24), 5);
            g.FillPath(card, cardPath);

            // Three compact orange marks suggest the capture/selection action.
            using var accent = new SolidBrush(Color.FromArgb(232, 83, 30));
            g.FillRectangle(accent, 8, 9, 4, 4);
            g.FillRectangle(accent, 8, 15, 4, 4);
            g.FillRectangle(accent, 8, 21, 4, 4);
            g.FillRectangle(accent, 13, 9, 4, 4);

            // Gray screen/document with a single blue focus ring.
            using var panel = new SolidBrush(Color.FromArgb(160, 168, 172));
            using var panelPath = RoundedRect(new Rectangle(13, 11, 11, 12), 2);
            g.FillPath(panel, panelPath);

            using var bluePen = new Pen(Color.FromArgb(0, 132, 211), 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(bluePen, 22, 22, 25, 25);
            g.DrawEllipse(bluePen, 24, 23, 6, 6);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var source = Icon.FromHandle(handle);
            return (Icon)source.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
        path.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
        path.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
