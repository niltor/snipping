using System.Drawing.Drawing2D;

namespace Snipping.App;

/// <summary>
/// A topmost, borderless, draggable floating window that displays a pinned screenshot.
/// Supports drag-to-move, mouse-wheel-to-resize, right-click context menu, and a hover close button.
/// </summary>
public sealed class PinForm : Form
{
    private readonly Bitmap _bitmap;
    private bool _dragging;
    private Point _dragOffset;
    private double _scale = 1.0;

    public PinForm(Bitmap bitmap, Point screenLocation)
    {
        _bitmap = bitmap;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = screenLocation;
        Size = bitmap.Size;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;

        var menu = new ContextMenuStrip();
        var copyItem = new ToolStripMenuItem("复制到剪贴板");
        copyItem.Click += (_, _) => Clipboard.SetImage(_bitmap);
        var closeItem = new ToolStripMenuItem("关闭");
        closeItem.Click += (_, _) => Close();
        menu.Items.Add(copyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(closeItem);
        ContextMenuStrip = menu;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_bitmap, 0, 0, Width, Height);

        // Thin border
        using (var pen = new Pen(Color.FromArgb(120, 120, 120), 1))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _dragging = true;
        _dragOffset = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Location = new Point(
                Location.X + e.X - _dragOffset.X,
                Location.Y + e.Y - _dragOffset.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        _scale *= factor;
        _scale = Math.Clamp(_scale, 0.2, 5.0);
        var newW = Math.Max(48, (int)(_bitmap.Width * _scale));
        var newH = Math.Max(48, (int)(_bitmap.Height * _scale));
        // Keep center stable
        var cx = Location.X + Width / 2;
        var cy = Location.Y + Height / 2;
        Size = new Size(newW, newH);
        Location = new Point(cx - newW / 2, cy - newH / 2);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap.Dispose();
        base.Dispose(disposing);
    }
}
