using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Snipping.App;

public sealed class RoundedButton : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 6;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color IdleColor { get; set; } = Color.FromArgb(38, 38, 38);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor { get; set; } = Color.FromArgb(55, 55, 55);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedColor { get; set; } = Color.FromArgb(70, 70, 70);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedColor { get; set; } = Color.FromArgb(0, 90, 158);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsSelected { get; set; }

    /// <summary>Border drawn around the button when selected (e.g. white ring for color dots).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedBorderColor { get; set; } = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedBorderWidth { get; set; } = 2;

    /// <summary>
    /// GDI+ icon painter: (Graphics g, Rectangle contentBounds, Color iconColor)
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<Graphics, Rectangle, Color>? IconPainter { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int IconPadding { get; set; } = 1;

    private bool _isHovered;
    private bool _isPressed;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Size = new Size(36, 32);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _isHovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _isHovered = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _isPressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _isPressed = false; Invalidate(); }

    protected override void OnPaintBackground(PaintEventArgs pevent) { /* suppress default */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Clear entire control area to parent background to avoid artifacts
        g.Clear(Parent?.BackColor ?? Color.FromArgb(30, 30, 30));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, CornerRadius);

        // Always paint a solid opaque background — no transparency
        var bg = IsSelected ? SelectedColor
               : _isPressed ? PressedColor
               : _isHovered ? HoverColor
               : IdleColor;

        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);

        // Selected indicator
        if (IsSelected && SelectedBorderColor.A > 0)
        {
            // Ring border (for color dots)
            var inset = new Rectangle(
                SelectedBorderWidth / 2, SelectedBorderWidth / 2,
                Width - SelectedBorderWidth - 1, Height - SelectedBorderWidth - 1);
            using var borderPath = RoundedRect(inset, Math.Max(1, CornerRadius - 1));
            using var borderPen = new Pen(SelectedBorderColor, SelectedBorderWidth);
            g.DrawPath(borderPen, borderPath);
        }

        // Icon or text
        if (IconPainter is not null)
        {
            var content = ClientRectangle;
            content.Inflate(-IconPadding, -IconPadding);
            IconPainter(g, content, ForeColor);
        }
        else if (!string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
